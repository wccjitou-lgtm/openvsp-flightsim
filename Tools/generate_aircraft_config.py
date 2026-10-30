#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""
generate_aircraft_config.py — 合成 AircraftConfig.json (飞行模拟器输入)

输入 (OpenVSP/VSPAERO 数据管线产物):
  1. *_MassProps.txt     —— 质量属性 (parse_massprops.py)
  2. *.stab              —— 稳定性导数, STABILITY_DEFAULT 模式 (parse_vspaero_stab.py)
  3. *.polar             —— alpha 扫描极曲线 (用于 CD = CD0 + k*CL^2 拟合)

输出: AircraftConfig.json, 结构见开发提示词 3.4 节。

符号约定 (重要, 写入 JSON 的 meta 字段):
  - 导数表中角度类导数 (alpha/beta/舵偏) 均为 per rad
  - 角速度类导数已按无量纲率 (q_hat = q*cref/(2V), p_hat/r_hat 用 bref)
  - CL/CD/CS/CMl/CMm/CMn 为 VSPAERO 打印的标准稳定性系数
  - 舵面正偏转 = VSPAERO ControlSurfaceGroup 正增益方向:
      升降舵 +: 后缘向下 (标准约定, ΔCL>0, ΔCm<0, 低头)
      副翼   +: 滚转力矩系数增量为负 (CMl<0)
      方向舵 +: 侧力系数增量 CY<0, 偏航力矩 CMn>0
  - 坐标系: OpenVSP 机体轴 (X 后, Y 右, Z 上); 转换到 Unity 坐标系是引擎层的职责

用法:
  python generate_aircraft_config.py --name TestUAV \
      --massprops Data/TestUAV_MassProps.txt \
      --stab Data/TestUAV.stab \
      [--polar Data/TestUAV.polar] \
      [--max-thrust 48] \
      [--out Data/AircraftConfig.json]
"""

from __future__ import annotations

import argparse
import json
import math
import re
import sys
from pathlib import Path
from typing import Any, Dict, List, Optional, Tuple

sys.path.insert(0, str(Path(__file__).resolve().parent))

from parse_massprops import parse_massprops
from parse_vspaero_stab import parse_stab

# 舵面最大偏角 (deg), 开发提示词 3.4 节的默认值
MAX_DEFLECTION = {"elevator": 25.0, "aileron": 20.0, "rudder": 25.0}

# 起落架定义 (标准机体轴 x 前/y 右/z 下, 原点=重心; Phase 2 弹簧-阻尼模型)
# TestUAV 前三点: 前轮可转向, 主轮带刹车。静压缩 ~5cm 承全重。
GEAR_DEFAULT = {
    "model": "springDamper",
    "wheels": [
        {"name": "nose", "position_m": [1.05, 0.0, 0.35], "kN_per_m": 2000.0,
         "cNs_per_m": 180.0, "steerable": True, "brake": False},
        {"name": "mainL", "position_m": [-0.10, 0.55, 0.35], "kN_per_m": 2000.0,
         "cNs_per_m": 180.0, "steerable": False, "brake": True},
        {"name": "mainR", "position_m": [-0.10, -0.55, 0.35], "kN_per_m": 2000.0,
         "cNs_per_m": 180.0, "steerable": False, "brake": True},
    ],
    "friction": {"mu_lateral": 0.7, "mu_rolling": 0.02, "mu_brake": 0.45,
                 "maxSteerDeg": 25.0},
}

# Explicit axis tokens, including the previously supported abbreviations.
CONTROL_MATCH = [
    ("elevator", ("elevator", "elev")),
    ("aileron", ("aileron", "ail")),
    ("rudder", ("rudder", "rud")),
]


def find_control_groups(control_groups: Dict[str, str], key: str) -> List[str]:
    """Find all explicitly named axis groups; never infer an axis from 'tail'."""
    if key not in dict(CONTROL_MATCH):
        raise ValueError(f"Unknown control axis: {key}")
    matches = []
    for con_key, name in control_groups.items():
        # Split CamelCase before case-folding; delimiters and digits also bound tokens.
        words = re.sub(r"([A-Z]+)([A-Z][a-z])", r"\1 \2", name)
        words = re.sub(r"([a-z0-9])([A-Z])", r"\1 \2", words)
        tokens = set(re.findall(r"[a-z]+", words.lower()))
        axes = [axis for axis, aliases in CONTROL_MATCH if tokens.intersection(aliases)]
        if len(axes) > 1 or (not axes and tokens.intersection(("elevon", "ruddervator"))):
            raise ValueError(
                f"Ambiguous control group {con_key} ({name!r}); "
                "provide separately named elevator/aileron/rudder axis groups"
            )
        if key in axes:
            matches.append(con_key)
    return matches


def find_control_group(control_groups: Dict[str, str], key: str) -> Optional[str]:
    """Compatibility helper for a single group; do not silently discard others."""
    matches = find_control_groups(control_groups, key)
    if len(matches) > 1:
        raise ValueError(f"Found multiple {key} groups: {matches}; use find_control_groups")
    return matches[0] if matches else None


def parse_polar(path: str | Path) -> List[Dict[str, float]]:
    """解析 VSPAERO .polar 文件, 返回按 (Mach,AoA,Beta) 去重后的工况列表 (取最后一次迭代)。

    polar 列 (VSPAERO 7.2.2): Iter Mach AoA Beta CLo CLi CLtot CDo CDi CDtot ...
    """
    p = Path(path)
    if not p.is_file():
        raise FileNotFoundError(f".polar 文件不存在: {p}")
    lines = p.read_text(encoding="utf-8", errors="replace").splitlines()

    header_idx = None
    columns: List[str] = []
    for i, line in enumerate(lines):
        content = line.strip().lstrip("#").strip()
        if "CLtot" in content and "AoA" in content and "CDtot" in content:
            columns = content.split()
            header_idx = i
            break
    if header_idx is None:
        raise ValueError(f"未在 {p.name} 中找到 polar 表头")

    def col(name: str) -> Optional[int]:
        try:
            return columns.index(name)
        except ValueError:
            return None

    # VSPAERO .polar 列 (7.2.2): Beta Mach AoA Re/1e6 CLo CLi CLtot CDo CDi CDtot ...
    i_mach, i_aoa, i_beta = col("Mach"), col("AoA"), col("Beta")
    i_cl, i_cd = col("CLtot"), col("CDtot")
    i_cdw = col("CDwtot")   # CDo + 远场(Trefftz)诱导阻力; 近场 CDtot 非物理时的回退来源
    if None in (i_mach, i_aoa, i_cl, i_cd):
        raise ValueError(f"{p.name} 缺少必要列 Mach/AoA/CLtot/CDtot")

    cases: Dict[Tuple[float, float, float], Dict[str, float]] = {}
    for line in lines[header_idx + 1:]:
        tokens = line.split()
        if len(tokens) < len(columns):
            continue
        try:
            mach = float(tokens[i_mach])
            aoa = float(tokens[i_aoa])
            beta = float(tokens[i_beta])
            cl = float(tokens[i_cl])
            cd = float(tokens[i_cd])
        except ValueError:
            continue
        row = {"mach": mach, "aoa_deg": aoa, "beta_deg": beta, "CL": cl, "CD": cd}
        if i_cdw is not None:
            try:
                row["CDw"] = float(tokens[i_cdw])
            except ValueError:
                pass
        cases[(mach, aoa, beta)] = row
    return sorted(cases.values(), key=lambda c: (c["beta_deg"], c["aoa_deg"]))


def fit_drag_polar(cases: List[Dict[str, float]],
                   key: str = "CD") -> Tuple[Optional[float], Optional[float]]:
    """对 beta=0 的 alpha 扫描做最小二乘拟合 CD = CD0 + k*CL^2。

    key: 阻力取值列 ("CD" = 近场 CDtot; "CDw" = 远场 CDwtot)。
    返回 (CD0, k); 点数不足 (或缺该列) 时返回 (None, None)。
    """
    pts = [(c["CL"], c[key]) for c in cases if abs(c["beta_deg"]) < 1e-9 and key in c]
    if len(pts) < 3:
        return None, None
    n = len(pts)
    sx = sum(cl * cl for cl, _ in pts)          # x = CL^2
    sy = sum(cd for _, cd in pts)
    sxx = sum((cl * cl) ** 2 for cl, _ in pts)
    sxy = sum((cl * cl) * cd for cl, cd in pts)
    denom = n * sxx - sx * sx
    if abs(denom) < 1e-300:
        return None, None
    k = (n * sxy - sx * sy) / denom
    cd0 = (sy - k * sx) / n
    return cd0, k


def parse_polar_raw(path: str | Path) -> Tuple[List[str], List[List[float]]]:
    """解析 polar 原始数值列 (返回列名与行数据)。"""
    p = Path(path)
    if not p.is_file():
        raise FileNotFoundError(f".polar 文件不存在: {p}")
    lines = p.read_text(encoding="utf-8", errors="replace").splitlines()
    columns: List[str] = []
    rows: List[List[float]] = []
    for line in lines:
        content = line.strip().lstrip("#").strip()
        if not columns and "CLtot" in content and "AoA" in content and "CDtot" in content:
            columns = content.split()
            continue
        tokens = line.split()
        if columns and len(tokens) >= len(columns):
            try:
                rows.append([float(t) for t in tokens])
            except ValueError:
                continue
    if not columns:
        raise ValueError(f"未在 {p.name} 中找到 polar 表头")
    return columns, rows


def fit_lateral_beta(polar_file: Path) -> Optional[Dict[str, float]]:
    """从 beta 扫描极曲线回归横侧向静导数 (per rad)。

    背景: .stab 的 Beta 扰动步长仅 0.01 度, 有限差分噪声淹没小量级导数
    (实测 Cnbeta 偏小 ~20 倍)。beta 扫描 (±6 度 5 点) 的线性回归更可靠。
    VSPAERO 体轴: X 后/Y 右/Z 上; Cl = -dCMx/dβ, Cn = -dCMz/dβ。

    返回 {"CYbeta","Clbeta","Cnbeta"} 或 None。
    """
    try:
        columns, rows = parse_polar_raw(polar_file)
    except (FileNotFoundError, ValueError):
        return None
    idx = {n: i for i, n in enumerate(columns)}
    if not all(k in idx for k in ("Beta", "CStot", "CMxtot", "CMztot")):
        return None
    pts = [(r[idx["Beta"]], r[idx["CStot"]], r[idx["CMxtot"]], r[idx["CMztot"]]) for r in rows]
    if len(pts) < 3:
        return None

    def linfit(getter):
        n = len(pts)
        sx = sy = sxx = sxy = 0.0
        for p_ in pts:
            x, y = p_[0], getter(p_)
            sx += x; sy += y; sxx += x * x; sxy += x * y
        denom = n * sxx - sx * sx
        if abs(denom) < 1e-300:
            return None
        return (n * sxy - sx * sy) / denom  # per deg

    rad = 57.29577951308232
    d_cs = linfit(lambda p_: p_[1])
    d_cmx = linfit(lambda p_: p_[2])
    d_cmz = linfit(lambda p_: p_[3])
    if None in (d_cs, d_cmx, d_cmz):
        return None
    return {
        "CYbeta": d_cs * rad,
        "Clbeta": -d_cmx * rad,
        "Cnbeta": -d_cmz * rad,
    }


def generate(name: str, massprops_file: Path, stab_file: Path,
             polar_file: Optional[Path], beta_polar_file: Optional[Path],
             max_thrust_N: Optional[float],
             mesh_path: str) -> Dict[str, Any]:
    mp = parse_massprops(massprops_file)
    stab = parse_stab(stab_file)

    ref = stab["reference"]
    dv = stab["derivatives"]
    cgs = stab["control_groups"]

    # ---- 横侧向静导数: 优先用 beta 扫描回归 (.stab 的 0.01 度扰动噪声大) ----
    lateral_fit = fit_lateral_beta(beta_polar_file) if beta_polar_file else None
    lateral_stab = {
        "CYbeta": dv["CS"]["Beta"],
        "Clbeta": dv["CMl"]["Beta"],
        "Cnbeta": dv["CMn"]["Beta"],
    }
    if lateral_fit is not None:
        lateral_used = lateral_fit
        print(f"横侧静导数采用 beta 扫描回归: CYb={lateral_fit['CYbeta']:.4f} "
              f"Clb={lateral_fit['Clbeta']:.4f} Cnb={lateral_fit['Cnbeta']:.4f} /rad")
        print(f"  (.stab 交叉参考:            CYb={lateral_stab['CYbeta']:.4f} "
              f"Clb={lateral_stab['Clbeta']:.4f} Cnb={lateral_stab['Cnbeta']:.4f} /rad)")
    else:
        lateral_used = lateral_stab

    # ---- 质量属性 (来自 MassProps) ----
    mass = mp["totals"]["mass_kg"]
    inertia = mp["totals"]["inertia_kgm2"]
    cg_mp = mp["totals"]["cg_m"]

    # 交叉校验: .stab 的力矩参考点应与 MassProps 重心一致 (容差 5 cm)
    cg_stab = [ref.get("Xcg", 0.0), ref.get("Ycg", 0.0), ref.get("Zcg", 0.0)]
    cg_mismatch = max(abs(a - b) for a, b in zip(cg_mp, cg_stab))
    if cg_mismatch > 0.05:
        print(f"警告: MassProps 重心与 .stab Xcg 偏差 {cg_mismatch:.4f} m > 0.05 m", file=sys.stderr)

    # ---- 状态点与 CL0/Cm0 推算 ----
    alpha_trim_deg = ref.get("AoA", 0.0)
    alpha_trim = math.radians(alpha_trim_deg)
    CL0 = dv["CL"]["base"] - dv["CL"]["Alpha"] * alpha_trim
    Cm0 = dv["CMm"]["base"] - dv["CMm"]["Alpha"] * alpha_trim

    # ---- 极曲线拟合 ----
    cd0_fit, k_fit = (None, None)
    polar_cases: List[Dict[str, float]] = []
    clalpha_polar = None
    drag_fallback = None
    if polar_file is not None:
        polar_cases = parse_polar(polar_file)
        cd0_fit, k_fit = fit_drag_polar(polar_cases)
        # 近场 CDtot (表面压力积分) 在后掠/锥形薄面上会算出负的诱导阻力, 拟合得 CD0<0 或 k<0
        # (非物理, 校验器会拒收整机)。此时改用远场 CDwtot = CDo + Trefftz 面诱导阻力重拟合;
        # 仅当重拟合物理 (CD0>=0 且 k>=0) 才采用, 否则维持原值交给校验器报错。
        if cd0_fit is not None and (cd0_fit < 0 or k_fit < 0):
            cd0_w, k_w = fit_drag_polar(polar_cases, key="CDw")
            if cd0_w is not None and cd0_w >= 0 and k_w >= 0:
                drag_fallback = {"source": "CDwtot", "nearFieldCD0": cd0_fit,
                                 "nearFieldK": k_fit}
                print(f"警告: 近场 CDtot 拟合非物理 (CD0={cd0_fit:.4f}, k={k_fit:.4f}), "
                      f"改用远场 CDwtot: CD0={cd0_w:.4f}, k={k_w:.4f}")
                cd0_fit, k_fit = cd0_w, k_w
        # 从 polar 线性拟合 CLalpha 做交叉校验
        pts = [(math.radians(c["aoa_deg"]), c["CL"]) for c in polar_cases
               if abs(c["beta_deg"]) < 1e-9]
        if len(pts) >= 2:
            n = len(pts)
            sx = sum(a for a, _ in pts)
            sy = sum(cl for _, cl in pts)
            sxx = sum(a * a for a, _ in pts)
            sxy = sum(a * cl for a, cl in pts)
            denom = n * sxx - sx * sx
            if abs(denom) > 1e-300:
                clalpha_polar = (n * sxy - sx * sy) / denom

    # ---- 操纵导数 ----
    columns = set(stab.get("derivative_columns", []))
    for row in dv.values():
        columns.update(row)
    unmapped = sorted(col for col in columns if col.startswith("ConGrp_") and col not in cgs)
    if unmapped:
        raise ValueError(f"Unmapped control derivative columns: {unmapped}")
    control_fields = {
        "elevator": {"CLde": "CL", "Cmde": "CMm"},
        "aileron": {"CYda": "CS", "Clda": "CMl", "Cnda": "CMn"},
        "rudder": {"CYdr": "CS", "Cldr": "CMl", "Cndr": "CMn"},
    }
    authority_fields = {"elevator": "Cmde", "aileron": "Clda", "rudder": "Cndr"}
    controls: Dict[str, Dict[str, float]] = {}
    for key, fields in control_fields.items():
        groups = find_control_groups(cgs, key)
        if not groups:
            raise ValueError(f"Missing control group for {key}")
        derivs = {}
        for output, coef in fields.items():
            values = []
            for con_key in groups:
                value = dv.get(coef, {}).get(con_key)
                if not isinstance(value, (int, float)) or not math.isfinite(value):
                    raise ValueError(
                        f"Incomplete or nonfinite {key} derivative: "
                        f"{con_key} ({cgs[con_key]!r}) / {coef}"
                    )
                values.append(value)
            # VSPAERO vspaero.C perturbs gain * (group angle + delta), then
            # divides by group delta. Gains are already in .stab: sum signed
            # group derivatives for a common axis command, without a second
            # side-dependent sign, gain multiplication, average, or abs().
            derivs[output] = sum(values)
        authority = authority_fields[key]
        if not all(math.isfinite(value) for value in derivs.values()) or derivs[authority] == 0:
            raise ValueError(
                f"Unusable {key} control mapping: {groups}, "
                f"nonfinite derivatives or zero {authority}; check solver group gains"
            )
        derivs["maxDeflectionDeg"] = MAX_DEFLECTION[key]
        controls[key] = derivs

    # ---- 发动机 (简化模型) ----
    if max_thrust_N is None:
        max_thrust_N = round(0.35 * mass * 9.80665, 2)  # 默认推重比 0.35

    config: Dict[str, Any] = {
        "name": name,
        "units": {"length": "m", "mass": "kg", "inertia": "kg*m^2",
                  "angle": "rad", "force": "N"},
        "reference": {
            "Sref_m2": ref["Sref"],
            "bref_m": ref["Bref"],
            "cref_m": ref["Cref"],
            "cg_m": cg_mp,
        },
        "mass": {
            "mass_kg": mass,
            "inertia_kgm2": inertia,
        },
        "aero": {
            # 状态点信息 (导数线性化中心)
            "trimPoint": {
                "mach": ref.get("Mach"),
                "alpha_deg": alpha_trim_deg,
                "beta_deg": ref.get("Beta", 0.0),
                "note": "导数在该状态点线性化",
            },
            "CL0": CL0,
            "CLalpha": dv["CL"]["Alpha"],
            "CLq": dv["CL"]["q"],
            "CD0": cd0_fit if cd0_fit is not None else dv["CD"]["base"],
            "k_induced": k_fit if k_fit is not None else None,
            "Cm0": Cm0,
            "Cmalpha": dv["CMm"]["Alpha"],
            "Cmq": dv["CMm"]["q"],
            "CYbeta": lateral_used["CYbeta"],
            "Clbeta": lateral_used["Clbeta"],
            "Cnbeta": lateral_used["Cnbeta"],
            "CYp": dv["CS"]["p"],
            "Clp": dv["CMl"]["p"],
            "Cnp": dv["CMn"]["p"],
            "CYr": dv["CS"]["r"],
            "Clr": dv["CMl"]["r"],
            "Cnr": dv["CMn"]["r"],
            "controls": controls,
        },
        "engine": {
            "type": "simpleThrust",
            "maxThrust_N": max_thrust_N,
            "note": "T = maxThrust_N * throttle * f(V), f(V)=1-clamp(V/Vref,0,0.5), Vref=60 m/s",
        },
        "gear": GEAR_DEFAULT,
        "geometryMeshPath": mesh_path,
        "meta": {
            "source": {
                "massprops": str(massprops_file),
                "stab": str(stab_file),
                "polar": str(polar_file) if polar_file else None,
                "beta_polar": str(beta_polar_file) if beta_polar_file else None,
            },
            "lateralStaticDerivatives": {
                "source": "beta_sweep_regression" if lateral_fit is not None else "stab",
                "stabCrosscheck": lateral_stab,
            },
            "staticMargin": stab["results"].get("SM", {}).get("value"),
            "neutralPoint_m": stab["results"].get("X_np", {}).get("value"),
            # 尾翼几何 (数据源 build_model.as, 用于尾容量系数计算):
            #   平尾: 半展 0.7, 根弦 0.30, 锥比 0.7, 原点 X=2.45 (机头后, OpenVSP X 后向)
            #   垂尾: 高 0.55, 根弦 0.35, 锥比 0.6, 原点 X=2.45
            #   尾力臂 = 平尾气动中心(X原点+0.25根弦) - cg
            "design": {
                "htailHalfSpan_m": 0.7, "htailRootChord_m": 0.30, "htailTaper": 0.7,
                "vtailHeight_m": 0.55, "vtailRootChord_m": 0.35, "vtailTaper": 0.6,
                "tailX_m": 2.45,
                "htailArm_m": round(2.45 + 0.30 * 0.25 - cg_mp[0], 3),   # AC(0.25c) - cg
            },
            "clalpha_polar_crosscheck": clalpha_polar,
            "signConventions": {
                "angularRates": "无量纲率: q_hat=q*cref/(2V), p_hat/r_hat=p*bref/(2V)",
                "angleDerivatives": "per rad",
                "elevator": "+ 偏转 = 后缘向下 (标准约定, ΔCL>0, ΔCm<0 低头; 镜像侧增益-1后实测 CLde>0, Cmde<0)",
                "aileron": "+ 偏转 = 滚转力矩系数增量为负 (CMl 减小)",
                "rudder": "+ 偏转 = CY<0, CMn>0",
                "bodyAxes": "OpenVSP 机体轴 (X 后, Y 右, Z 上); gear.position_m 除外 (标准体轴 x 前/y 右/z 下)",
            },
        },
    }
    if drag_fallback is not None:
        config["meta"]["dragPolar"] = drag_fallback
    return config


def main(argv: Optional[List[str]] = None) -> int:
    ap = argparse.ArgumentParser(description="生成 AircraftConfig.json")
    ap.add_argument("--name", required=True, help="机型名 (写入 JSON)")
    ap.add_argument("--massprops", required=True, type=Path, help="*_MassProps.txt 路径")
    ap.add_argument("--stab", required=True, type=Path, help="*.stab 路径")
    ap.add_argument("--polar", type=Path, default=None, help="*.polar 路径 (alpha 扫描, 阻力极曲线拟合)")
    ap.add_argument("--beta-polar", type=Path, default=None,
                    help="*.polar 路径 (beta 扫描, 横侧静导数回归; 推荐, 见 fit_lateral_beta 注释)")
    ap.add_argument("--max-thrust", type=float, default=None, help="最大推力 N (默认按推重比 0.35)")
    ap.add_argument("--mesh", default="Meshes/TestUAV.stl", help="Unity 网格相对路径")
    ap.add_argument("--out", type=Path, default=Path("Data/AircraftConfig.json"), help="输出路径")
    args = ap.parse_args(argv)

    try:
        config = generate(args.name, args.massprops, args.stab, args.polar, args.beta_polar,
                          args.max_thrust, args.mesh)
    except (FileNotFoundError, ValueError, KeyError) as e:
        print(f"错误: {e}", file=sys.stderr)
        return 1

    args.out.parent.mkdir(parents=True, exist_ok=True)
    with open(args.out, "w", encoding="utf-8") as f:
        json.dump(config, f, indent=2, ensure_ascii=False)
    print(f"已生成 {args.out}")

    # 摘要
    a = config["aero"]
    print(f"  mass={config['mass']['mass_kg']:.3f} kg  "
          f"Sref={config['reference']['Sref_m2']}  CLa={a['CLalpha']:.4f}/rad  "
          f"Cma={a['Cmalpha']:.4f}/rad  CD0={a['CD0'] if a['CD0'] is None else round(a['CD0'], 5)}  "
          f"k={a['k_induced'] if a['k_induced'] is None else round(a['k_induced'], 5)}")
    if config["meta"]["clalpha_polar_crosscheck"] is not None:
        print(f"  交叉校验: CLalpha(polar 拟合)={config['meta']['clalpha_polar_crosscheck']:.4f}"
              f"  vs  CLalpha(stab)={a['CLalpha']:.4f}")
    return 0


if __name__ == "__main__":
    sys.exit(main())
