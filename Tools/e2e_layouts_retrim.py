#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""
e2e_layouts_retrim.py — 用 S3 实测 Xnp (VSPAERO) 对 D/E 二次配重, 重跑 S4+S5。

第一轮配重用工坊 estimate_np (估算值在非常规布局上系统性偏前), D/E 重心过前;
本脚本闭环: 气动算完用实测 Xnp 重新配平 (与用户在工坊里的正确操作一致)。
"""
from __future__ import annotations

import json
import sys
import time
import xml.etree.ElementTree as ET
from pathlib import Path

try:
    sys.stdout.reconfigure(encoding="utf-8", errors="replace")
except Exception:
    pass

sys.path.insert(0, str(Path(__file__).resolve().parent))
import e2e_workshop_test as base
import e2e_workshop_layouts as lay  # noqa: F401  (side effect: base.OUT 指到 e2e_layouts)

OUT = base.OUT
SM_TARGET = {"D": 0.10, "E": 0.13}


def ballast_x(vsp3: Path, name: str) -> float:
    """从 vsp3 读配重几何的 XForm/X_Rel_Location。"""
    txt = vsp3.read_text(encoding="utf-8")
    i = txt.find(f"<Name>{name}</Name>")
    if i < 0:
        raise RuntimeError(f"{vsp3.name} 找不到 {name}")
    blk = txt[i:i + 60000]
    m = re_search_xform(blk)
    return m


def re_search_xform(blk: str) -> float:
    import re
    m = re.search(r'<X_Rel_Location Value="([^"]+)"', blk)
    if not m:
        raise RuntimeError("X_Rel_Location 未找到")
    return float(m.group(1))


def main() -> int:
    py = base.find_python313()
    base.RESULTS.clear()
    aircraft: dict[str, dict] = {}

    w = base.Worker(py)
    try:
        for key, bal_name in (("D", "DBallast"), ("E", "EBallast")):
            vsp3 = OUT / f"E2E_{key}.vsp3"
            aero = json.loads((OUT / f"aero_{key}" / "aero_result.json").read_text(encoding="utf-8"))
            xnp, mac = aero["Xnp_m"], aero["cref"]
            cur_x = ballast_x(vsp3, bal_name)
            w.call("open", {"path": str(vsp3).replace("\\", "/")})
            m0 = w.call("mass_props")
            bal_mass = m0["per_geom"][bal_name]["mass_kg"]
            target_cg = xnp - SM_TARGET[key] * mac
            dx = (target_cg - m0["cg_m"][0]) * m0["mass_kg"] / bal_mass
            new_x = min(max(cur_x + dx, 0.05), 2.4)
            # open 的 vsp3 无 sidecar 时槽位靠启发式分配, ballast 不一定入槽 — 按名字找
            geoms = w.call("list_geoms")["geoms"]
            bal_gid = next(g["id"] for g in geoms if bal_name in g["name"])
            base.set_parms(w, bal_gid, [("X_Location", "XForm", new_x)])
            m1 = w.call("mass_props")
            print(f"  [二次配重] 机型{key}: 配重 {cur_x:.3f} → {new_x:.3f} m, "
                  f"CG {m0['cg_m'][0]:.3f} → {m1['cg_m'][0]:.3f} "
                  f"(实测 Xnp={xnp:.3f}, MAC={mac:.3f}, 目标 SM={SM_TARGET[key] * 100:.0f}%)")
            w.call("save", {"path": str(vsp3).replace("\\", "/")})
            aircraft[key] = {"vsp3": str(vsp3), "mass": m1, "np": {"mac_m": mac}}
    finally:
        w.close()

    print("\n== S4 换机管线 (二次配重后) ==")
    for key, info in aircraft.items():
        try:
            t0 = time.time()
            r = base.stage_pipeline(py, key, info)
            if not r["ok"]:
                base.check(f"S4 机型{key} 换机管线", False, r["error"])
                continue
            info["config_dir"] = r["dir"]
            cfg = r["config"]
            sm = (cfg.get("meta") or {}).get("staticMargin")
            mass = (cfg.get("mass") or {}).get("mass_kg")
            m_ws = info["mass"].get("mass_kg", 0)
            mdiff = abs(mass - m_ws) / m_ws * 100 if mass and m_ws else 999
            base.check(f"S4 机型{key} 换机管线", True,
                       f"SM={sm} 质量差 {mdiff:.2f}% ({time.time() - t0:.0f}s)")
        except Exception as e:
            base.check(f"S4 机型{key} 换机管线", False, str(e))

    print("\n== S5 飞行 (二次配重后) ==")
    for key, info in aircraft.items():
        if "config_dir" not in info:
            base.check(f"S5 机型{key} 飞行", False, "无 config")
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
    (OUT / "e2e_retrim_summary.json").write_text(json.dumps(
        {"passed": passed, "total": total, "results": base.RESULTS},
        ensure_ascii=False, indent=1), encoding="utf-8")
    print(f"\n== 二次配重汇总: {passed}/{total} ==")
    return 0 if passed == total else 1


if __name__ == "__main__":
    sys.exit(main())
