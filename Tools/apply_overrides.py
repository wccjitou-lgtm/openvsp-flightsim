#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""
apply_overrides.py — Phase 8a: 机型工坊部件级覆盖 (生成阶段, 纯 Python)

在 generate_aircraft_config 产出 config 后应用用户覆盖:
  1. ballast / cg_override —— 配重与重心直接指定 (OpenVSP 坐标 X 向后):
     精确换算质量/重心/惯量 (平行轴定理), 纵向导数按新参考点移轴
     (Cm0/Cmalpha/Cmde += Δx_ov·CLx/cref, 与线性气动模型自洽)
  2. gear.auto —— 按几何包围盒 + 重心自动生成三点式起落架
     (config 体轴约定: 相对 CG, x 前 y 右 z 下; 刚度按质量缩放)
  3. mass_scale_total 等汇总级缩放

部件级覆盖 (role/control/mass_scale/incidence/ignore) 在 prep 的
AngelScript 里烘焙执行 (见 import_vsp.render_prep), 不经过本模块。

用法 (被 import_vsp.py 调用; 也可独立):
  python apply_overrides.py <AircraftConfig.json> <overrides.json> <bbox.json>
  bbox.json = {"min":[x,y,z],"max":[x,y,z]} (OpenVSP 坐标, 来自 STL)
"""

from __future__ import annotations

import json
import math
import sys
from pathlib import Path

# 起落架基准 (U8 重设计): 恒定静沉陷 GEAR_SAG_M — 三轮合成刚度
# k_total = m·g/sag → 固有角频率 √(g/sag) ≈ 25.6 rad/s 与质量无关,
# RK4 子步 (h=1/480) ωh≈0.053 恒稳定; 且不再继承按质量比例缩放的
# ~6cm 静沉陷 (原 14.83kg×900N/m 基准, 小飞机会半埋进跑道)。
GEAR_SAG_M = 0.015
GEAR_ZETA = 0.55
# 机腹净空: 曾尝试轮子放在包围盒底之下抬机腹 (2026-10-10), 但接触模型的
# 俯仰恢复刚度 Σk·x² 对深起落架 + 高阻/无尾布局会锁死抬前轮 (E2E_F 轮深
# ≥0.16m 实测卡死) — 在接触模型修好前保持 0 (轮子贴包围盒底)。
GEAR_CLEARANCE_M = 0.0


def apply_ballast(cfg: dict, ballast_kg: float, pos_ov: list[float]) -> None:
    """加配重 (OpenVSP 坐标): 质量/重心/惯量精确换算, 纵向导数移轴。"""
    m = cfg["mass"]["mass_kg"]
    cg = cfg["reference"]["cg_m"]
    mb = float(ballast_kg)
    bx, by, bz = (float(v) for v in pos_ov)
    new_m = m + mb
    new_cg = [(m * cg[i] + mb * p) / new_m for i, p in enumerate((bx, by, bz))]

    # 惯量: I(新CG) = I(旧CG) + m·d_old² + mb·d_b² (各轴分量)
    d_old = [cg[i] - new_cg[i] for i in range(3)]
    d_b = [p - new_cg[i] for i, p in enumerate((bx, by, bz))]
    I = cfg["mass"]["inertia_kgm2"]
    # OpenVSP 惯量分量: Ixx(俯仰-? x轴滚转), Iyy, Izz + 积项
    # 对角: Ixx+=m(dy²+dz²)+mb(dyb²+dzb²) 等; 积项 Ixy+=m·dx·dy+... (近似)
    mo, bo = m, mb
    I["Ixx"] += mo * (d_old[1] ** 2 + d_old[2] ** 2) + bo * (d_b[1] ** 2 + d_b[2] ** 2)
    I["Iyy"] += mo * (d_old[0] ** 2 + d_old[2] ** 2) + bo * (d_b[0] ** 2 + d_b[2] ** 2)
    I["Izz"] += mo * (d_old[0] ** 2 + d_old[1] ** 2) + bo * (d_b[0] ** 2 + d_b[1] ** 2)
    I["Ixy"] += mo * d_old[0] * d_old[1] + bo * d_b[0] * d_b[1]
    I["Ixz"] += mo * d_old[0] * d_old[2] + bo * d_b[0] * d_b[2]
    I["Iyz"] += mo * d_old[1] * d_old[2] + bo * d_b[1] * d_b[2]

    _shift_cg(cfg, new_cg)
    cfg["mass"]["mass_kg"] = new_m


def apply_cg_override(cfg: dict, cg_ov: list[float]) -> None:
    """直接指定重心 (OpenVSP 坐标)。惯量保持 (无法反推部件分布, 精确惯量
    应配合 ballast 使用); 纵向导数仍按移轴更新。"""
    _shift_cg(cfg, [float(v) for v in cg_ov])


def _shift_cg(cfg: dict, new_cg: list[float]) -> None:
    old = cfg["reference"]["cg_m"]
    dx_ov = new_cg[0] - old[0]           # OpenVSP X 向后为正
    cfg["reference"]["cg_m"] = list(new_cg)
    cref = cfg["reference"]["cref_m"]
    a = cfg["aero"]
    # 移轴: Cm_x' = Cm_x + Δx_ov·CL_x/cref (CG 后移→静稳定性减弱)
    a["Cm0"] += dx_ov * a["CL0"] / cref
    a["Cmalpha"] += dx_ov * a["CLalpha"] / cref
    elev = a.get("controls", {}).get("elevator")
    if elev:
        elev["Cmde"] += dx_ov * elev["CLde"] / cref


def auto_gear(cfg: dict, bbox: dict) -> None:
    """按包围盒 + 重心生成三点起落架 (体轴: 相对 CG, x 前 y 右 z 下)。"""
    mass = cfg["mass"]["mass_kg"]
    cg = cfg["reference"]["cg_m"]
    x0, y0, z0 = bbox["min"]
    x1, y1, z1 = bbox["max"]
    length = x1 - x0                      # OpenVSP X 向后: x0=机头
    width = y1 - y0

    def body(x_ov, y_ov, z_ov):
        # ovsp(X后,Y右,Z上) → 体轴相对 CG (x前,y右,z下)
        return [cg[0] - x_ov, y_ov - cg[1], -(z_ov - cg[2])]

    nose_x = x0 + 0.12 * length           # 机头往后 12% 长
    main_x = cg[0] + 0.10                 # 主轮略后于重心 (ovsp)
    track = max(0.8 * width, 0.12 * (y1 - y0) + width) if width > 0.2 else width
    track = max(width, 0.5)               # 轮距: 不窄于机身宽
    # 机腹最低点再让出净空: 轮子曾贴包围盒底 (=机腹贴地), 跑道面/地面会把
    # 机身下部视觉吞掉 ("模型埋在地里", 2026-10-10 飞翼实测)。15cm 起落架净空。
    ground_z = z0 - GEAR_CLEARANCE_M

    k_total = mass * 9.81 / GEAR_SAG_M
    c_total = 2.0 * GEAR_ZETA * math.sqrt(k_total * mass)
    k = k_total / 2.6          # 主轮 (三轮配比 nose 0.6 : main 1 : main 1)
    c = c_total / 2.6
    wheels = [
        {"name": "nose", "position_m": body(nose_x, 0.0, ground_z),
         "kN_per_m": k * 0.6, "cNs_per_m": c * 0.6, "steerable": True, "brake": False},
        {"name": "mainL", "position_m": body(main_x, track * 0.5, ground_z),
         "kN_per_m": k, "cNs_per_m": c, "steerable": False, "brake": True},
        {"name": "mainR", "position_m": body(main_x, -track * 0.5, ground_z),
         "kN_per_m": k, "cNs_per_m": c, "steerable": False, "brake": True},
    ]
    cfg["gear"] = {"wheels": wheels, "friction": {
        "mu_lateral": 0.7, "mu_rolling": 0.02, "mu_brake": 0.45, "maxSteerDeg": 25.0}}


def parse_component_rows(mp_path: Path) -> list[dict]:
    """MassProps.txt 部件表 → 有序行列表 (同名部件保留多行):
    [{name, mass, cg:[x,y,z], I:{Ixx..Iyz}}] (OpenVSP 坐标)。"""
    rows = []
    in_table = False
    for raw in mp_path.read_text(encoding="utf-8", errors="replace").splitlines():
        t = raw.split()
        if len(t) >= 12 and t[0] == "Name" and t[1] == "Mass":
            in_table = True
            continue
        if not in_table or len(t) < 12:
            continue
        if t[0] == "Totals":
            break
        try:
            rows.append({
                "name": t[0],
                "mass": float(t[1]),
                "cg": [float(t[2]), float(t[3]), float(t[4])],
                "I": {"Ixx": float(t[5]), "Iyy": float(t[6]), "Izz": float(t[7]),
                      "Ixy": float(t[8]), "Ixz": float(t[9]), "Iyz": float(t[10])},
            })
        except ValueError:
            continue
    return rows


def apply_component_weights(cfg: dict, mp_path: Path,
                            weights: dict[str, float]) -> None:
    """手动部件重量 (按名, 同名部件质量合计设为该值, 内部按原比例分摊):
    由 MassProps 部件表精确重组全机质量/重心/惯量, 纵向导数随新 CG 移轴。
    未指定的部件保持其 MassProp 质量。"""
    rows = parse_component_rows(mp_path)
    if not rows:
        raise ValueError("No MassProps component rows available for weight overrides")
    for name, target in weights.items():
        target = float(target)
        if not math.isfinite(target) or target <= 0:
            raise ValueError(f"Component weight must be finite and positive: {name}")
        matched = [r for r in rows if r["name"] in (name, name + "_pm")]
        if not matched:
            raise ValueError(f"No mass record for component: {name}")
        cur = sum(r["mass"] for r in matched)
        if cur <= 1e-9:
            share = target / len(matched)
            for r in matched:
                r["mass"] = share
        else:
            k = float(target) / cur
            for r in matched:
                r["mass"] *= k

    new_m = sum(r["mass"] for r in rows)
    if new_m <= 1e-9:
        return
    new_cg = [sum(r["mass"] * r["cg"][i] for r in rows) / new_m for i in range(3)]

    # 惯量: 部件惯量保持 + 平行轴到新 CG (重量改动不大时近似良好)
    Itot = {k: 0.0 for k in ("Ixx", "Iyy", "Izz", "Ixy", "Ixz", "Iyz")}
    for r in rows:
        if r["mass"] <= 1e-9:
            continue
        d = [r["cg"][i] - new_cg[i] for i in range(3)]
        Itot["Ixx"] += r["I"]["Ixx"] + r["mass"] * (d[1] ** 2 + d[2] ** 2)
        Itot["Iyy"] += r["I"]["Iyy"] + r["mass"] * (d[0] ** 2 + d[2] ** 2)
        Itot["Izz"] += r["I"]["Izz"] + r["mass"] * (d[0] ** 2 + d[1] ** 2)
        Itot["Ixy"] += r["I"]["Ixy"] + r["mass"] * d[0] * d[1]
        Itot["Ixz"] += r["I"]["Ixz"] + r["mass"] * d[0] * d[2]
        Itot["Iyz"] += r["I"]["Iyz"] + r["mass"] * d[1] * d[2]

    cfg["mass"]["mass_kg"] = new_m
    cfg["mass"]["inertia_kgm2"].update(Itot)
    _shift_cg(cfg, new_cg)


def apply_overrides(cfg_path: Path, overrides: dict, bbox: dict,
                    mp_path: Path | None = None) -> list[str]:
    """应用全部生成阶段覆盖, 原地更新 config 文件; 返回已应用的项列表。"""
    cfg = json.loads(cfg_path.read_text(encoding="utf-8"))
    applied = []

    weights = overrides.get("component_weights")
    if weights and mp_path and mp_path.is_file():
        apply_component_weights(cfg, mp_path,
                                {k: float(v) for k, v in weights.items()})
        applied.append(f"component_weights {weights}")

    if overrides.get("ballast_kg"):
        pos = overrides.get("ballast_pos_m", [cfg["reference"]["cg_m"][0] - 1.0, 0, 0])
        apply_ballast(cfg, overrides["ballast_kg"], pos)
        applied.append(f"ballast {overrides['ballast_kg']}kg @ {pos}")

    if overrides.get("cg_override_m"):
        apply_cg_override(cfg, overrides["cg_override_m"])
        applied.append(f"cg_override {overrides['cg_override_m']}")

    if (overrides.get("gear") or {}).get("auto"):
        auto_gear(cfg, bbox)
        applied.append("gear auto-generated")

    if applied:
        cfg_path.write_text(json.dumps(cfg, ensure_ascii=False, indent=2),
                            encoding="utf-8")
    return applied


def parse_stl_bbox(stl_path: Path) -> dict:
    """ASCII STL 顶点包围盒 (OpenVSP 坐标)。"""
    mn = [math.inf] * 3
    mx = [-math.inf] * 3
    for line in stl_path.read_text(encoding="utf-8", errors="replace").splitlines():
        t = line.split()
        if len(t) == 4 and t[0] == "vertex":
            for i in range(3):
                v = float(t[i + 1])
                mn[i] = min(mn[i], v)
                mx[i] = max(mx[i], v)
    return {"min": mn, "max": mx}


def main(argv=None) -> int:
    import argparse
    ap = argparse.ArgumentParser()
    ap.add_argument("config", type=Path)
    ap.add_argument("overrides", type=Path)
    ap.add_argument("bbox", type=Path, help="bbox.json (或 STL 文件自动解析)")
    args = ap.parse_args(argv)
    ov = json.loads(args.overrides.read_text(encoding="utf-8"))
    if args.bbox.suffix.lower() == ".stl":
        bbox = parse_stl_bbox(args.bbox)
    else:
        bbox = json.loads(args.bbox.read_text(encoding="utf-8"))
    applied = apply_overrides(args.config, ov, bbox)
    for a in applied:
        print("applied:", a)
    return 0


if __name__ == "__main__":
    sys.exit(main())
