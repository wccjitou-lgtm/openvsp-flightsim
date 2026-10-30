#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""
e2e_workshop_test.py — 机型工坊端到端全面测试驱动 (2026-10-07 审查用)。

链路 (与 Unity 工坊实际操作一致, 只是由脚本代替点击):
  S1 建模   worker 子进程: new/load_example/add_part/set_parms/set_mass/
             add_control → mass_props + estimate_np + export_meshes → save vsp3
  S2 持久化 重开保存的 vsp3, 核对几何/操纵面数量
  S3 气动   workshop_aero.py (独立 vspscript 进程, 迎角 0→10° × 3)
  S4 换机   import_vsp.py 全管线 → VspImports/E2E_* (config + obj + report)
  S5 飞行   ConsoleFlightSim.exe --headless 60 (满油门起飞→爬升→改平)

产出: work/e2e_test/e2e_summary.json + 控制台逐项 PASS/FAIL。
用法: py -3.13 Tools/e2e_workshop_test.py [--skip-flight] [--skip-pipeline]
"""
from __future__ import annotations

import argparse
import json
import os
import shutil
import subprocess
import sys
import time
from pathlib import Path

# Windows 控制台默认 GBK: 输出含 ²/中文时必须切 UTF-8
try:
    sys.stdout.reconfigure(encoding="utf-8", errors="replace")
    sys.stderr.reconfigure(encoding="utf-8", errors="replace")
except Exception:
    pass

ROOT = Path(__file__).resolve().parent.parent
OUT = ROOT / "work" / "e2e_test"
VSPIMPORTS = ROOT / "VspImports"

RESULTS: list[dict] = []


def check(name: str, ok: bool, detail: str = "") -> bool:
    RESULTS.append({"name": name, "ok": bool(ok), "detail": detail})
    print(("  [PASS] " if ok else "  [FAIL] ") + name + (" — " + detail if detail else ""))
    return ok


# ---------------------------------------------------------------- worker 客户端
def find_python313() -> str:
    cands = [
        os.path.expandvars(r"%LOCALAPPDATA%\Programs\Python\Python313\python.exe"),
        r"C:\Python313\python.exe",
    ]
    for c in cands:
        if Path(c).is_file():
            return c
    out = subprocess.run(["py", "-3.13", "-c", "import sys;print(sys.executable)"],
                         capture_output=True, text=True)
    if out.returncode == 0 and out.stdout.strip():
        return out.stdout.strip().splitlines()[-1]
    raise RuntimeError("找不到 Python 3.13 (worker 硬依赖)")


class Worker:
    def __init__(self, py: str):
        env = dict(os.environ, PYTHONIOENCODING="utf-8")
        self.proc = subprocess.Popen(
            [py, str(ROOT / "Tools" / "workshop_worker.py")],
            stdin=subprocess.PIPE, stdout=subprocess.PIPE, stderr=subprocess.DEVNULL,
            text=True, encoding="utf-8", env=env, cwd=str(ROOT))
        self._id = 0
        self.logs: list[str] = []

    def call(self, cmd: str, args: dict | None = None) -> dict:
        self._id += 1
        self.proc.stdin.write(json.dumps(
            {"id": self._id, "cmd": cmd, "args": args or {}}, ensure_ascii=False) + "\n")
        self.proc.stdin.flush()
        while True:
            line = self.proc.stdout.readline()
            if not line:
                raise RuntimeError(f"worker 进程退出 (cmd={cmd})")
            line = line.strip()
            if not line:
                continue
            try:
                r = json.loads(line)
            except json.JSONDecodeError:
                self.logs.append(line)
                continue
            if r.get("id") != self._id:
                self.logs.append(line)
                continue
            if not r.get("ok"):
                raise RuntimeError(f"{cmd} 失败: {r.get('error')}")
            return r.get("data") or {}

    def close(self):
        try:
            self.call("shutdown")
        except Exception:
            pass
        try:
            self.proc.wait(timeout=5)
        except Exception:
            self.proc.kill()


# ---------------------------------------------------------------- 机型定义
def geom_of(w: Worker, slot: str) -> str:
    data = w.call("list_geoms")
    gid = (data.get("slots") or {}).get(slot)
    if not gid:
        raise RuntimeError(f"槽位 {slot} 未分配")
    return gid


def set_parms(w: Worker, gid: str, parms: list[tuple[str, str, float]]):
    w.call("set_parms", {"geom_id": gid,
                         "parms": [{"name": n, "group": g, "value": v} for n, g, v in parms]})


def build_aircraft(w: Worker, key: str) -> dict:
    """返回 {vsp3, sref, bref, cref, cg, mass, subsurfs}。"""
    w.call("new")
    if key in ("A", "B"):
        w.call("load_example")
    if key == "B":
        # 示例改型: 主翼加大 + 扭转/上反, 机身加长, 平尾加大
        wing = geom_of(w, "wing1")
        set_parms(w, wing, [("Span", "XSec_1", 1.3), ("Tip_Chord", "XSec_1", 0.18),
                            ("Twist", "XSec_1", -2.0), ("Dihedral", "XSec_1", 4.0)])
        fus = geom_of(w, "fuselage")
        set_parms(w, fus, [("Length", "Design", 1.9)])
        ht = geom_of(w, "htail")
        set_parms(w, ht, [("Span", "XSec_1", 0.28), ("X_Location", "XForm", 1.65)])
        vt = geom_of(w, "vtail")
        set_parms(w, vt, [("X_Location", "XForm", 1.65)])
        bal = geom_of(w, "ballast")
        set_parms(w, bal, [("X_Location", "XForm", 0.60)])
        # 改型后 CG 移到 NP 之后 (静不稳定) — 按工坊工作流用配重把 CG 拉回
        # SM≈12% MAC: Δx_ballast = (CG_target − CG) × M / m_ballast
        np0 = w.call("estimate_np")
        m0 = w.call("mass_props")
        mac = np0.get("mac_m", 0.2)
        target_cg = np0["xnp_m"] - 0.12 * mac
        m_tot = m0["mass_kg"]
        m_bal = 0.5   # 示例机配重 (load_example PointMass)
        dx = (target_cg - m0["cg_m"][0]) * m_tot / m_bal
        new_x = min(max(0.60 + dx, 0.05), 1.8)
        set_parms(w, bal, [("X_Location", "XForm", new_x)])
        print(f"  [机型B] 配重前移 0.60 → {new_x:.3f} m (目标 CG={target_cg:.3f})")
    elif key == "C":
        # 全自建 3m 级 UAV: 逐槽位 add_part + 参数 + 三轴操纵面 + 质量
        # 顺序: 几何参数 → mass_props(刷新体积/面积缓存) → set_mass → 操纵面
        # (set_mass 的密度换算依赖缓存; 改几何后缓存过期会失真 — e2e 实测 12.6kg≠6kg)
        # 注: 部件名不得含 "_" (VSPAERO 操纵面 tag 匹配会断 — e2e 实测)
        fus = w.call("add_part", {"slot": "fuselage", "name": "CFuselage"})["slots"]["fuselage"]
        wing = w.call("add_part", {"slot": "wing1", "name": "CWing"})["slots"]["wing1"]
        ht = w.call("add_part", {"slot": "htail", "name": "CHTail"})["slots"]["htail"]
        vt = w.call("add_part", {"slot": "vtail", "name": "CVTail"})["slots"]["vtail"]
        bal = w.call("add_part", {"slot": "ballast", "name": "CBallast"})["slots"]["ballast"]

        w.call("fuselage_set_radii", {"geom_id": fus,
                                      "diameters": [0, 0.16, 0.20, 0.20, 0.15, 0.07],
                                      "resample_to": 6})   # 粗机身(0.30)作薄面进
        # VSPAERO 阻力解非物理 (CD 随迎角变负) — e2e 实测, 细机身正常
        set_parms(w, fus, [("Length", "Design", 2.4)])
        set_parms(w, wing, [("Span", "XSec_1", 1.5), ("Root_Chord", "XSec_1", 0.35),
                            ("Tip_Chord", "XSec_1", 0.20), ("Sweep", "XSec_1", 6.0),
                            ("Dihedral", "XSec_1", 3.0), ("X_Location", "XForm", 0.75)])
        set_parms(w, ht, [("Span", "XSec_1", 0.60), ("Root_Chord", "XSec_1", 0.22),
                          ("Tip_Chord", "XSec_1", 0.14), ("X_Location", "XForm", 2.15)])
        set_parms(w, vt, [("Span", "XSec_1", 0.55), ("Root_Chord", "XSec_1", 0.24),
                          ("Tip_Chord", "XSec_1", 0.15), ("X_Location", "XForm", 2.15)])
        set_parms(w, bal, [("X_Location", "XForm", 0.10)])

        w.call("mass_props")   # 几何定型后刷新缓存
        w.call("set_mass", {"geom_id": fus, "mass_kg": 2.5})
        w.call("set_mass", {"geom_id": wing, "mass_kg": 1.8})
        w.call("set_mass", {"geom_id": ht, "mass_kg": 0.25})
        w.call("set_mass", {"geom_id": vt, "mass_kg": 0.20})
        w.call("set_mass", {"geom_id": bal, "mass_kg": 1.5})

        w.call("add_control", {"geom_id": wing, "role": "aileron",
                               "eta_start": 0.55, "eta_end": 0.95, "len_c": 0.25})
        w.call("add_control", {"geom_id": ht, "role": "elevator",
                               "eta_start": 0.10, "eta_end": 0.95, "len_c": 0.35})
        w.call("add_control", {"geom_id": vt, "role": "rudder",
                               "eta_start": 0.10, "eta_end": 0.95, "len_c": 0.35})

        # 配重配平 (同机型B, 15% MAC 余量): CG 目标 = Xnp − 15% MAC
        np0 = w.call("estimate_np")
        m0 = w.call("mass_props")
        target_cg = np0["xnp_m"] - 0.15 * np0.get("mac_m", 0.27)
        dx = (target_cg - m0["cg_m"][0]) * m0["mass_kg"] / 1.5
        new_x = min(max(0.10 + dx, 0.02), 2.3)
        set_parms(w, bal, [("X_Location", "XForm", new_x)])
        print(f"  [机型C] 配重 0.10 → {new_x:.3f} m (目标 CG={target_cg:.3f})")

    mass = w.call("mass_props")
    np_est = w.call("estimate_np")
    meshes = w.call("export_meshes")
    vsp3 = OUT / f"E2E_{key}.vsp3"
    w.call("save", {"path": str(vsp3).replace("\\", "/")})
    data = w.call("list_geoms")
    nsub = sum(len(g.get("subsurfs") or []) for g in data["geoms"])
    return {
        "vsp3": str(vsp3), "mass": mass, "np": np_est, "meshes": meshes,
        "geoms": len(data["geoms"]), "subsurfs": nsub,
    }


# ---------------------------------------------------------------- 外部工具
def run_cmd(argv: list[str], cwd: Path, timeout: int, logfile: Path) -> tuple[int, str]:
    env = dict(os.environ, PYTHONIOENCODING="utf-8")
    with open(logfile, "w", encoding="utf-8") as f:
        p = subprocess.run(argv, cwd=str(cwd), env=env, stdout=f, stderr=subprocess.STDOUT,
                           timeout=timeout)
    return p.returncode, logfile.read_text(encoding="utf-8", errors="replace")


def stage_aero(py: str, key: str, info: dict) -> dict:
    """S3: workshop_aero.py — 参考量取 worker 估算 + 重心 (与 Unity 面板「取自模型」一致)。"""
    work = OUT / f"aero_{key}"
    work.mkdir(parents=True, exist_ok=True)
    cg = info["mass"].get("cg_m") or [0, 0, 0]
    panel = {
        "thin_set": "shown", "thick_set": "none", "symmetry": False,
        "ncpu": 4, "fem2d": False, "tecplot": False,
        "wake": {"fixed": False, "iter": 3, "nodes": 8, "relax": 1.0,
                 "freeze": 10000, "implicit_start": 0},
        "cull": {"flag": True, "frac": 0.030},
        "alpha": [0.0, 10.0, 3], "beta": [0.0, 0.0, 1], "mach": [0.0, 0.0, 1],
        "recref": [1e7, 2e7, 1],
        "ref_mode": "manual", "ref_choice": "stot", "ref_wing_id": "",
        "sref": info["np"].get("sref_m2", 1.0), "bref": info["np"].get("bref_m", 1.0),
        "cref": info["np"].get("mac_m", 0.2),
        "xref": cg[0], "yref": cg[1], "zref": cg[2],
        "controls": [],
    }
    panel_path = work / "panel.json"
    panel_path.write_text(json.dumps(panel), encoding="utf-8")
    rc, log = run_cmd([py, str(ROOT / "Tools" / "workshop_aero.py"),
                       "--vsp", info["vsp3"], "--panel", str(panel_path),
                       "--work", str(work)], ROOT, 900, work / "aero.log")
    result = None
    for line in log.splitlines():
        if line.startswith("[done] "):
            result = line[7:].split(" ")[0]
    if not result or not Path(result).is_file():
        return {"ok": False, "error": "无 [done] 结果行", "log_tail": log.splitlines()[-8:]}
    rj = json.loads(Path(result).read_text(encoding="utf-8"))
    return {"ok": True, "result": rj, "path": result}


def stage_pipeline(py: str, key: str, info: dict) -> dict:
    """S4: import_vsp.py 全管线 → VspImports/E2E_<key>。
    --overrides gear.auto: 与 Unity「换机试飞」按钮同款 (U7: 默认起落架是 14kg
    TestUAV 固定值, 小/大机会"高跷"或压垮 — e2e 实测 B 不起飞/C 翻倒)。"""
    name = f"E2E_{key}"
    out_dir = VSPIMPORTS / name
    work = OUT / f"pipe_{key}"
    work.mkdir(parents=True, exist_ok=True)
    ov = work / "overrides.json"
    ov.write_text('{"gear": {"auto": true}}', encoding="utf-8")
    rc, log = run_cmd([py, str(ROOT / "Tools" / "import_vsp.py"),
                       "--vsp", info["vsp3"], "--name", name,
                       "--alpha", "2", "--mach", "0.06",
                       "--alpha-range=-4,10,7",   # 7 点拟合极曲线 (3 点拟合 k_induced
                       "--overrides", str(ov),    # 可能为负被拒 — 机型C 实测)
                       "--out-dir", str(out_dir)],
                      ROOT, 1200, work / "pipeline.log")
    cfg = out_dir / "AircraftConfig.json"
    rep = out_dir / "report.json"
    if not cfg.is_file():
        return {"ok": False, "error": "未生成 AircraftConfig.json",
                "log_tail": log.splitlines()[-10:]}
    cfgj = json.loads(cfg.read_text(encoding="utf-8"))
    return {"ok": True, "config": cfgj,
            "report": json.loads(rep.read_text(encoding="utf-8")) if rep.is_file() else None,
            "dir": str(out_dir)}


def stage_flight(key: str, cfg_dir: Path) -> dict:
    """S5: ConsoleFlightSim --headless 60 (exe 从 cwd 读 AircraftConfig.json)。"""
    fld = OUT / f"flight_{key}"
    fld.mkdir(parents=True, exist_ok=True)
    for f in ("ConsoleFlightSim.exe", "FlightDynamicsCore.dll"):
        shutil.copyfile(ROOT / "bin" / f, fld / f)
    shutil.copyfile(Path(cfg_dir) / "AircraftConfig.json", fld / "AircraftConfig.json")
    rc, log = run_cmd([str(fld / "ConsoleFlightSim.exe"), "--headless", "60"],
                      fld, 300, fld / "flight.log")
    tail = log.splitlines()[-6:]
    nan = any("NaN!" in l for l in log.splitlines())
    climbed = any("已爬升并改平巡航" in l for l in log.splitlines())
    state = next((l for l in reversed(log.splitlines()) if l.startswith("高度 ")), "")
    return {"ok": rc == 0 and not nan, "nan": nan, "climbed": climbed,
            "state": state, "tail": tail}


# ---------------------------------------------------------------- 主流程
def main() -> int:
    ap = argparse.ArgumentParser()
    ap.add_argument("--skip-pipeline", action="store_true")
    ap.add_argument("--skip-flight", action="store_true")
    ap.add_argument("--only", default="", help="只跑指定机型 (如 C), 且复用已有 vsp3 跳过建模")
    args = ap.parse_args()

    OUT.mkdir(parents=True, exist_ok=True)
    py = find_python313()
    print(f"[e2e] python={py}")
    print(f"[e2e] 输出目录: {OUT}")

    aircraft: dict[str, dict] = {}
    only = args.only.strip().upper()

    # ---------- S1+S2 建模与持久化 ----------
    print("\n== S1 建模 (worker) ==")
    w = Worker(py)
    try:
        ping = w.call("ping")
        check("worker 就绪", True, f"python={ping.get('python')} vsproot={ping.get('vsproot')}")
        if only:
            # 复用已有 vsp3, 只补算质量/中性点 (供气动面板参考量)
            vsp3 = OUT / f"E2E_{only}.vsp3"
            if not vsp3.is_file():
                raise RuntimeError(f"--only {only} 需要已存在的 {vsp3}")
            w.call("open", {"path": str(vsp3).replace("\\", "/")})
            mass = w.call("mass_props")
            np_est = w.call("estimate_np")
            data = w.call("list_geoms")
            aircraft[only] = {"vsp3": str(vsp3), "mass": mass, "np": np_est,
                              "geoms": len(data["geoms"]),
                              "subsurfs": sum(len(g.get("subsurfs") or [])
                                              for g in data["geoms"]),
                              "desc": "复用已有 vsp3"}
            print(f"  [复用] E2E_{only}.vsp3 几何 {aircraft[only]['geoms']}")
        for key, desc in (("A", "示例机基准"), ("B", "示例改型"), ("C", "全自建 3m 级")):
            if only:
                break
            try:
                t0 = time.time()
                info = build_aircraft(w, key)
                info["desc"] = desc
                aircraft[key] = info
                ok = check(f"S1 机型{key}({desc}) 建模+保存",
                           Path(info["vsp3"]).is_file() and info["geoms"] >= 4,
                           f"几何 {info['geoms']} 操纵面 {info['subsurfs']} "
                           f"质量 {info['mass'].get('mass_kg', 0):.2f} kg "
                           f"({time.time() - t0:.0f}s)")
                m = info["mass"]
                cg = m.get("cg_m") or [0, 0, 0]
                check(f"S1 机型{key} 质量属性", m.get("mass_kg", 0) > 0.5,
                      f"CG=({cg[0]:.3f},{cg[1]:.3f},{cg[2]:.3f})")
                xnp = info["np"].get("xnp_m")
                check(f"S1 机型{key} 中性点估算", xnp is not None and xnp == xnp,
                      f"Xnp={xnp if xnp is not None else float('nan'):.3f} m "
                      f"Sref={info['np'].get('sref_m2', 0):.3f} m²")
                nmesh = len([1 for s in ("fuselage", "wing1", "htail", "vtail", "ballast")
                             if (info["meshes"].get("meshes") or {}).get(s)])
                check(f"S1 机型{key} 分槽位网格导出", nmesh >= 4, f"{nmesh}/5 槽位有 STL")
            except Exception as e:
                check(f"S1 机型{key}({desc}) 建模", False, str(e))

        print("\n== S2 持久化 (保存→重开) ==")
        if not only:
            for key, info in aircraft.items():
                try:
                    data = w.call("open", {"path": info["vsp3"].replace("\\", "/")})
                    nsub = sum(len(g.get("subsurfs") or []) for g in data["geoms"])
                    check(f"S2 机型{key} 重开", len(data["geoms"]) == info["geoms"]
                          and nsub == info["subsurfs"],
                          f"几何 {len(data['geoms'])}/{info['geoms']} "
                          f"操纵面 {nsub}/{info['subsurfs']}")
                except Exception as e:
                    check(f"S2 机型{key} 重开", False, str(e))
    finally:
        w.close()

    # ---------- S3 气动 ----------
    print("\n== S3 VSPAERO 气动计算 (workshop_aero.py) ==")
    for key, info in aircraft.items():
        try:
            t0 = time.time()
            r = stage_aero(py, key, info)
            if not r["ok"]:
                check(f"S3 机型{key} 气动计算", False, r["error"])
                continue
            rj = r["result"]
            info["aero"] = rj
            alphas = rj.get("alpha") or []
            cls = rj.get("CL") or []
            dcm = rj.get("dCm_dCL")
            xnp = rj.get("Xnp_m")
            cl_alpha = None
            if len(alphas) >= 2 and len(cls) >= 2 and alphas[-1] != alphas[0]:
                cl_alpha = (cls[-1] - cls[0]) / (alphas[-1] - alphas[0])
            check(f"S3 机型{key} 气动计算", True,
                  f"CL={['%.3f' % c for c in cls]} @α={alphas} ({time.time() - t0:.0f}s)")
            check(f"S3 机型{key} CLα 合理 (0.04~0.14 /°)",
                  cl_alpha is not None and 0.04 < cl_alpha < 0.14,
                  f"CLα={cl_alpha if cl_alpha is None else round(cl_alpha, 4)} /°")
            check(f"S3 机型{key} 纵向静稳定 (dCm/dCL<0)",
                  dcm is not None and dcm < 0,
                  f"dCm/dCL={dcm} Xnp={xnp}")
        except Exception as e:
            check(f"S3 机型{key} 气动计算", False, str(e))

    # ---------- S4 换机管线 ----------
    if not args.skip_pipeline:
        print("\n== S4 换机管线 (import_vsp.py → AircraftConfig.json) ==")
        for key, info in aircraft.items():
            try:
                t0 = time.time()
                r = stage_pipeline(py, key, info)
                if not r["ok"]:
                    check(f"S4 机型{key} 换机管线", False, r["error"])
                    continue
                info["config_dir"] = r["dir"]
                cfg = r["config"]
                aero = cfg.get("aero") or {}
                mass = (cfg.get("mass") or {}).get("mass_kg")
                m_workshop = info["mass"].get("mass_kg", 0)
                mdiff = abs(mass - m_workshop) / m_workshop * 100 if mass and m_workshop else 999
                check(f"S4 机型{key} 换机管线", True, f"({time.time() - t0:.0f}s)")
                check(f"S4 机型{key} 质量一致性 (±1%)", mdiff <= 1.0,
                      f"工坊 {m_workshop:.3f} kg vs 管线 {mass} kg (差 {mdiff:.2f}%)")
                # 操纵面导数在 aero.controls.{elevator,aileron,rudder} 节 (e2e 实测键名)
                ctl = (aero.get("controls") or {})
                derivs = {
                    "CLalpha": aero.get("CLalpha"), "Cmalpha": aero.get("Cmalpha"),
                    "Cmde": (ctl.get("elevator") or {}).get("Cmde"),
                    "Clda": (ctl.get("aileron") or {}).get("Clda"),
                    "Cndr": (ctl.get("rudder") or {}).get("Cndr"),
                }
                missing = [k for k, v in derivs.items() if not v]
                check(f"S4 机型{key} 关键导数非零", not missing,
                      f"缺失: {missing}" if missing else
                      " ".join(f"{k}={v:.4g}" for k, v in derivs.items()))
                check(f"S4 机型{key} 诱导阻力因子非负", (aero.get("k_induced") or 0) >= 0,
                      f"k_induced={aero.get('k_induced')}")
                obj = Path(r["dir"]) / "model.obj"
                check(f"S4 机型{key} Unity 网格", obj.is_file() and obj.stat().st_size > 1000,
                      f"{obj.stat().st_size // 1024 if obj.is_file() else 0} KB")
            except Exception as e:
                check(f"S4 机型{key} 换机管线", False, str(e))

    # ---------- S5 飞行 ----------
    if not args.skip_flight and not args.skip_pipeline:
        print("\n== S5 飞行测试 (ConsoleFlightSim --headless 60) ==")
        for key, info in aircraft.items():
            if "config_dir" not in info:
                check(f"S5 机型{key} 飞行", False, "无 config (S4 失败)")
                continue
            try:
                r = stage_flight(key, info["config_dir"])
                check(f"S5 机型{key} 60s 自动飞行 (无 NaN)", r["ok"], r["state"])
                check(f"S5 机型{key} 起飞并爬升改平", r["climbed"],
                      "; ".join(r["tail"][-2:]))
            except Exception as e:
                check(f"S5 机型{key} 飞行", False, str(e))

    # ---------- 汇总 ----------
    total = len(RESULTS)
    passed = sum(1 for r in RESULTS if r["ok"])
    summary = {"passed": passed, "total": total, "results": RESULTS,
               "aircraft": {k: {"vsp3": v["vsp3"], "desc": v.get("desc"),
                                "config_dir": v.get("config_dir")}
                            for k, v in aircraft.items()}}
    (OUT / "e2e_summary.json").write_text(
        json.dumps(summary, ensure_ascii=False, indent=1), encoding="utf-8")
    print(f"\n== 汇总: {passed}/{total} 通过 ==  明细: {OUT / 'e2e_summary.json'}")
    return 0 if passed == total else 1


if __name__ == "__main__":
    sys.exit(main())
