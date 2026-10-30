#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""
e2e_workshop_layouts.py — 机型工坊多布局专项测试 (2026-10-10 审查用)。

在 e2e_workshop_test 的 A/B/C (常规布局) 之外, 覆盖三类特殊布局:
  D 飞翼: 无平尾/无垂尾, 主翼升降副翼混控 (Elevator+Aileron 同翼) + 翼尖小垂尾方向舵
        —— 考"缺槽位/缺轴"警告路径与混控布局
  E 大展弦比滑翔机: 全展 3.2m, AR≈16, 轻质量低速 —— 考低速配平与大上反
  F 高速小展弦比: 全展 1.2m, 后掠 28°, 翼载高 —— 考高速配平与大油门

链路同 e2e_workshop_test (worker 建模 → 持久化 → workshop_aero → import_vsp → 无头飞行)。
产出: work/e2e_layouts/e2e_layouts_summary.json。
用法: py -3.13 Tools/e2e_workshop_layouts.py [--skip-flight]
"""
from __future__ import annotations

import argparse
import json
import sys
import time
from pathlib import Path

try:
    sys.stdout.reconfigure(encoding="utf-8", errors="replace")
    sys.stderr.reconfigure(encoding="utf-8", errors="replace")
except Exception:
    pass

sys.path.insert(0, str(Path(__file__).resolve().parent))
import e2e_workshop_test as base   # 复用 Worker/stage_*/check

OUT = base.ROOT / "work" / "e2e_layouts"
base.OUT = OUT   # 复用的 stage_* 函数读模块全局 OUT, 改指到本测试的输出目录


def trim_ballast(w: base.Worker, bal_gid: str, bal_mass: float, sm_frac: float,
                 cur_x: float, xlim: tuple[float, float]) -> None:
    """按工坊工作流用配重把 CG 拉到 Xnp 前方 sm_frac·MAC 处。"""
    np0 = w.call("estimate_np")
    m0 = w.call("mass_props")
    mac = np0.get("mac_m", 0.25)
    target_cg = np0["xnp_m"] - sm_frac * mac
    dx = (target_cg - m0["cg_m"][0]) * m0["mass_kg"] / bal_mass
    new_x = min(max(cur_x + dx, xlim[0]), xlim[1])
    base.set_parms(w, bal_gid, [("X_Location", "XForm", new_x)])
    print(f"  [配重] {cur_x:.2f} → {new_x:.3f} m (目标 CG={target_cg:.3f}, "
          f"SM={sm_frac * 100:.0f}% MAC)")


def build_D_flyingwing(w: base.Worker) -> dict:
    """飞翼: 仅 主翼+配重+小垂尾(方向舵); 主翼带升降舵(内段)+副翼(外段) 混控。"""
    w.call("new")
    wing = w.call("add_part", {"slot": "wing1", "name": "DWing"})["slots"]["wing1"]
    fin = w.call("add_part", {"slot": "vtail", "name": "DFin"})["slots"]["vtail"]
    bal = w.call("add_part", {"slot": "ballast", "name": "DBallast"})["slots"]["ballast"]

    base.set_parms(w, wing, [("Span", "XSec_1", 1.4), ("Root_Chord", "XSec_1", 0.45),
                             ("Tip_Chord", "XSec_1", 0.25), ("Sweep", "XSec_1", 15.0),
                             ("X_Location", "XForm", 0.85)])
    base.set_parms(w, fin, [("Span", "XSec_1", 0.30), ("Root_Chord", "XSec_1", 0.20),
                            ("Tip_Chord", "XSec_1", 0.12), ("X_Location", "XForm", 1.55)])
    w.call("mass_props")
    w.call("set_mass", {"geom_id": wing, "mass_kg": 2.2})
    w.call("set_mass", {"geom_id": fin, "mass_kg": 0.15})
    w.call("set_mass", {"geom_id": bal, "mass_kg": 1.2})

    # 升降副翼混控: 内段升降舵 + 外段副翼 (同翼两面)
    w.call("add_control", {"geom_id": wing, "role": "elevator",
                           "eta_start": 0.08, "eta_end": 0.42, "len_c": 0.30})
    w.call("add_control", {"geom_id": wing, "role": "aileron",
                           "eta_start": 0.50, "eta_end": 0.95, "len_c": 0.25})
    w.call("add_control", {"geom_id": fin, "role": "rudder",
                           "eta_start": 0.10, "eta_end": 0.95, "len_c": 0.35})
    trim_ballast(w, bal, 1.2, 0.10, 0.30, (0.05, 1.2))
    return {"desc": "飞翼 (混控+小垂尾)"}


def build_E_glider(w: base.Worker) -> dict:
    """大展弦比滑翔机: 全展 3.2m AR≈16, 细机身, 轻质量。"""
    w.call("new")
    fus = w.call("add_part", {"slot": "fuselage", "name": "EFuselage"})["slots"]["fuselage"]
    wing = w.call("add_part", {"slot": "wing1", "name": "EWing"})["slots"]["wing1"]
    ht = w.call("add_part", {"slot": "htail", "name": "EHTail"})["slots"]["htail"]
    vt = w.call("add_part", {"slot": "vtail", "name": "EVTail"})["slots"]["vtail"]
    bal = w.call("add_part", {"slot": "ballast", "name": "EBallast"})["slots"]["ballast"]

    w.call("fuselage_set_radii", {"geom_id": fus,
                                  "diameters": [0, 0.10, 0.15, 0.15, 0.10, 0.05],
                                  "resample_to": 6})
    base.set_parms(w, fus, [("Length", "Design", 2.6)])
    base.set_parms(w, wing, [("Span", "XSec_1", 1.6), ("Root_Chord", "XSec_1", 0.24),
                             ("Tip_Chord", "XSec_1", 0.16), ("Dihedral", "XSec_1", 4.0),
                             ("X_Location", "XForm", 0.95), ("Z_Location", "XForm", 0.12)])
    base.set_parms(w, ht, [("Span", "XSec_1", 0.45), ("Root_Chord", "XSec_1", 0.20),
                           ("Tip_Chord", "XSec_1", 0.13), ("X_Location", "XForm", 2.30)])
    base.set_parms(w, vt, [("Span", "XSec_1", 0.35), ("Root_Chord", "XSec_1", 0.22),
                           ("Tip_Chord", "XSec_1", 0.14), ("X_Location", "XForm", 2.30)])

    w.call("mass_props")
    w.call("set_mass", {"geom_id": fus, "mass_kg": 1.6})
    w.call("set_mass", {"geom_id": wing, "mass_kg": 1.1})
    w.call("set_mass", {"geom_id": ht, "mass_kg": 0.18})
    w.call("set_mass", {"geom_id": vt, "mass_kg": 0.15})
    w.call("set_mass", {"geom_id": bal, "mass_kg": 0.8})

    w.call("add_control", {"geom_id": wing, "role": "aileron",
                           "eta_start": 0.55, "eta_end": 0.95, "len_c": 0.25})
    w.call("add_control", {"geom_id": ht, "role": "elevator",
                           "eta_start": 0.10, "eta_end": 0.95, "len_c": 0.35})
    w.call("add_control", {"geom_id": vt, "role": "rudder",
                           "eta_start": 0.10, "eta_end": 0.95, "len_c": 0.35})
    trim_ballast(w, bal, 0.8, 0.13, 0.30, (0.05, 2.2))
    return {"desc": "大展弦比滑翔机"}


def build_F_fast(w: base.Worker) -> dict:
    """高速小展弦比: 全展 1.2m, 后掠 28°, 翼载约为滑翔机的 2 倍。"""
    w.call("new")
    fus = w.call("add_part", {"slot": "fuselage", "name": "FFuselage"})["slots"]["fuselage"]
    wing = w.call("add_part", {"slot": "wing1", "name": "FWing"})["slots"]["wing1"]
    ht = w.call("add_part", {"slot": "htail", "name": "FHTail"})["slots"]["htail"]
    vt = w.call("add_part", {"slot": "vtail", "name": "FVTail"})["slots"]["vtail"]
    bal = w.call("add_part", {"slot": "ballast", "name": "FBallast"})["slots"]["ballast"]

    w.call("fuselage_set_radii", {"geom_id": fus,
                                  "diameters": [0, 0.09, 0.15, 0.15, 0.10, 0.05],
                                  "resample_to": 6})
    base.set_parms(w, fus, [("Length", "Design", 1.8)])
    base.set_parms(w, wing, [("Span", "XSec_1", 0.6), ("Root_Chord", "XSec_1", 0.32),
                             ("Tip_Chord", "XSec_1", 0.16), ("Sweep", "XSec_1", 28.0),
                             ("X_Location", "XForm", 0.75), ("Z_Location", "XForm", 0.08)])
    base.set_parms(w, ht, [("Span", "XSec_1", 0.30), ("Root_Chord", "XSec_1", 0.18),
                           ("Tip_Chord", "XSec_1", 0.11), ("X_Location", "XForm", 1.55)])
    base.set_parms(w, vt, [("Span", "XSec_1", 0.28), ("Root_Chord", "XSec_1", 0.20),
                           ("Tip_Chord", "XSec_1", 0.12), ("X_Location", "XForm", 1.55)])

    w.call("mass_props")
    w.call("set_mass", {"geom_id": fus, "mass_kg": 1.4})
    w.call("set_mass", {"geom_id": wing, "mass_kg": 0.9})
    w.call("set_mass", {"geom_id": ht, "mass_kg": 0.15})
    w.call("set_mass", {"geom_id": vt, "mass_kg": 0.12})
    w.call("set_mass", {"geom_id": bal, "mass_kg": 0.6})

    w.call("add_control", {"geom_id": wing, "role": "aileron",
                           "eta_start": 0.55, "eta_end": 0.95, "len_c": 0.25})
    w.call("add_control", {"geom_id": ht, "role": "elevator",
                           "eta_start": 0.10, "eta_end": 0.95, "len_c": 0.35})
    w.call("add_control", {"geom_id": vt, "role": "rudder",
                           "eta_start": 0.10, "eta_end": 0.95, "len_c": 0.35})
    trim_ballast(w, bal, 0.6, 0.13, 0.30, (0.05, 1.6))
    return {"desc": "高速小展弦比"}


def main() -> int:
    ap = argparse.ArgumentParser()
    ap.add_argument("--skip-flight", action="store_true")
    args = ap.parse_args()

    OUT.mkdir(parents=True, exist_ok=True)
    py = base.find_python313()
    print(f"[layouts] python={py}  输出: {OUT}")
    base.RESULTS.clear()

    aircraft: dict[str, dict] = {}

    print("\n== S1 建模 (worker) ==")
    w = base.Worker(py)
    try:
        ping = w.call("ping")
        base.check("worker 就绪", True, f"python={ping.get('python')}")
        for key, fn in (("D", build_D_flyingwing), ("E", build_E_glider),
                        ("F", build_F_fast)):
            try:
                t0 = time.time()
                info = fn(w)
                vsp3 = OUT / f"E2E_{key}.vsp3"
                w.call("save", {"path": str(vsp3).replace("\\", "/")})
                info["vsp3"] = str(vsp3)
                data = w.call("list_geoms")
                info["geoms"] = len(data["geoms"])
                info["subsurfs"] = sum(len(g.get("subsurfs") or []) for g in data["geoms"])
                info["mass"] = w.call("mass_props")
                info["np"] = w.call("estimate_np")
                info["meshes"] = w.call("export_meshes")
                aircraft[key] = info
                m = info["mass"]
                base.check(f"S1 机型{key}({info['desc']}) 建模+保存",
                           vsp3.is_file() and info["geoms"] >= 2,
                           f"几何 {info['geoms']} 操纵面 {info['subsurfs']} "
                           f"质量 {m.get('mass_kg', 0):.2f} kg "
                           f"CGx={m.get('cg_m', [0])[0]:.3f} ({time.time() - t0:.0f}s)")
                base.check(f"S1 机型{key} 中性点估算",
                           info["np"].get("xnp_m") is not None,
                           f"Xnp={info['np'].get('xnp_m')} Sref={info['np'].get('sref_m2')}")
            except Exception as e:
                base.check(f"S1 机型{key} 建模", False, str(e))

        print("\n== S2 持久化 (保存→重开) ==")
        for key, info in aircraft.items():
            try:
                data = w.call("open", {"path": info["vsp3"].replace("\\", "/")})
                nsub = sum(len(g.get("subsurfs") or []) for g in data["geoms"])
                base.check(f"S2 机型{key} 重开",
                           len(data["geoms"]) == info["geoms"] and nsub == info["subsurfs"],
                           f"几何 {len(data['geoms'])}/{info['geoms']} "
                           f"操纵面 {nsub}/{info['subsurfs']}")
            except Exception as e:
                base.check(f"S2 机型{key} 重开", False, str(e))
    finally:
        w.close()

    print("\n== S3 VSPAERO 气动 (workshop_aero.py) ==")
    for key, info in aircraft.items():
        try:
            t0 = time.time()
            r = base.stage_aero(py, key, info)
            if not r["ok"]:
                base.check(f"S3 机型{key} 气动计算", False, r["error"])
                continue
            rj = r["result"]
            info["aero"] = rj
            alphas, cls = rj.get("alpha") or [], rj.get("CL") or []
            cl_alpha = ((cls[-1] - cls[0]) / (alphas[-1] - alphas[0])
                        if len(alphas) >= 2 and alphas[-1] != alphas[0] else None)
            base.check(f"S3 机型{key} 气动计算", True,
                       f"CL={['%.3f' % c for c in cls]} ({time.time() - t0:.0f}s)")
            base.check(f"S3 机型{key} CLα 合理 (0.04~0.14 /°)",
                       cl_alpha is not None and 0.04 < cl_alpha < 0.14,
                       f"CLα={round(cl_alpha, 4) if cl_alpha else None} /°")
            dcm = rj.get("dCm_dCL")
            base.check(f"S3 机型{key} 纵向静稳定 (dCm/dCL<0)",
                       dcm is not None and dcm < 0,
                       f"dCm/dCL={dcm} Xnp={rj.get('Xnp_m')}")
        except Exception as e:
            base.check(f"S3 机型{key} 气动计算", False, str(e))

    print("\n== S4 换机管线 (import_vsp.py) ==")
    for key, info in aircraft.items():
        try:
            t0 = time.time()
            r = base.stage_pipeline(py, key, info)
            if not r["ok"]:
                base.check(f"S4 机型{key} 换机管线", False, r["error"])
                continue
            info["config_dir"] = r["dir"]
            cfg, aero = r["config"], r["config"].get("aero") or {}
            ctl = aero.get("controls") or {}
            mass = (cfg.get("mass") or {}).get("mass_kg")
            m_ws = info["mass"].get("mass_kg", 0)
            mdiff = abs(mass - m_ws) / m_ws * 100 if mass and m_ws else 999
            base.check(f"S4 机型{key} 换机管线", True, f"({time.time() - t0:.0f}s)")
            base.check(f"S4 机型{key} 质量一致性 (±1%)", mdiff <= 1.0,
                       f"工坊 {m_ws:.3f} vs 管线 {mass} kg (差 {mdiff:.2f}%)")
            derivs = {"CLalpha": aero.get("CLalpha"), "Cmalpha": aero.get("Cmalpha"),
                      "Cmde": (ctl.get("elevator") or {}).get("Cmde"),
                      "Clda": (ctl.get("aileron") or {}).get("Clda"),
                      "Cndr": (ctl.get("rudder") or {}).get("Cndr")}
            missing = [k for k, v in derivs.items() if not v]
            base.check(f"S4 机型{key} 关键导数非零", not missing,
                       f"缺失: {missing}" if missing else
                       " ".join(f"{k}={v:.4g}" for k, v in derivs.items()))
            base.check(f"S4 机型{key} 诱导阻力因子非负",
                       (aero.get("k_induced") or 0) >= 0,
                       f"k_induced={aero.get('k_induced')}")
            sm = (cfg.get("meta") or {}).get("staticMargin")
            print(f"    静裕度 SM={sm}")
        except Exception as e:
            base.check(f"S4 机型{key} 换机管线", False, str(e))

    if not args.skip_flight:
        print("\n== S5 飞行 (ConsoleFlightSim --headless 60) ==")
        for key, info in aircraft.items():
            if "config_dir" not in info:
                base.check(f"S5 机型{key} 飞行", False, "无 config (S4 失败)")
                continue
            try:
                r = base.stage_flight(key, Path(info["config_dir"]))
                base.check(f"S5 机型{key} 60s 自动飞行 (无 NaN)", r["ok"], r["state"])
                base.check(f"S5 机型{key} 起飞并爬升改平", r["climbed"],
                           "; ".join(r["tail"][-2:]))
            except Exception as e:
                base.check(f"S5 机型{key} 飞行", False, str(e))

    total = len(base.RESULTS)
    passed = sum(1 for r in base.RESULTS if r["ok"])
    summary = {"passed": passed, "total": total, "results": base.RESULTS,
               "aircraft": {k: {"vsp3": v.get("vsp3"), "desc": v.get("desc"),
                                "config_dir": v.get("config_dir")}
                            for k, v in aircraft.items()}}
    (OUT / "e2e_layouts_summary.json").write_text(
        json.dumps(summary, ensure_ascii=False, indent=1), encoding="utf-8")
    print(f"\n== 汇总: {passed}/{total} 通过 ==  明细: {OUT / 'e2e_layouts_summary.json'}")
    return 0 if passed == total else 1


if __name__ == "__main__":
    sys.exit(main())
