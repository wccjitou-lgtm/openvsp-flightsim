#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""
parse_vspaero_stab.py — 解析 VSPAERO 稳定性分析输出 (.stab 文件, STABILITY_DEFAULT 模式)

文件格式 (VSPAERO 7.2.2 / OpenVSP 3.51.3 实测, 见 Data/TestUAV.stab):

  1) 头部块: "# Name Value Units" 后跟 参考量行 (Sref_/Cref_/Bref_/Xcg_/.../Vinf_/角速度)
  2) 扰动表: "Case Delta Units CFx ... CMn" 表头 + 各扰动工况行
     (Base_Aero / Alpha / Beta / Roll__Rate / Pitch_Rate / Yaw___Rate / Mach / 控制面组)
  3) 导数表 (核心): "Coef Total Alpha Beta p q r Mach U ConGrp_1..N" 表头
     + CFx/CFy/CFz/CMx/CMy/CMz/CL/CD/CS/CMl/CMm/CMn 各行
     * 导数列已是无量纲化形式: 角度类 per rad; 角速度类按无量纲率
       q_hat = q*cref/(2V), p_hat/r_hat = p*bref/(2V) (从扰动数据反推验证过)
  4) 结果块: "# Result Value Units" + SM (静稳定裕度) / X_np (中立点)

导数表中的 ConGrp_N 列顺序与扰动表中的控制面组行一致,
本解析器自动建立 ConGrp_N -> 控制面组名 (如 MainWing_Aileron) 的映射。

用法:
  python parse_vspaero_stab.py <model.stab> [--json]
"""

from __future__ import annotations

import argparse
import json
import sys
from pathlib import Path
from typing import Any, Dict, List, Optional, Tuple

# 导数表的标准扰动列名 (ConGrp_N 之前的部分)
STD_DERIV_COLS = ["Alpha", "Beta", "p", "q", "r", "Mach", "U"]

# 扰动表中的标准 (非控制面) 工况名
STD_PERTURB_CASES = {
    "Base_Aero", "Alpha", "Beta", "Roll__Rate", "Pitch_Rate", "Yaw___Rate", "Mach", "U"
}

# 扰动表/导数表的系数列
PERTURB_COEF_COLS = ["CFx", "CFy", "CFz", "CMx", "CMy", "CMz",
                     "CL", "CD", "CS", "CMl", "CMm", "CMn"]


def _to_float(token: str) -> Optional[float]:
    try:
        return float(token)
    except ValueError:
        return None


def _parse_name_value_block(lines: List[str], start: int) -> Tuple[Dict[str, Tuple[float, str]], int]:
    """解析 "名称 数值 单位" 块, 从 start 开始, 遇到注释行 (#) 结束。"""
    out: Dict[str, Tuple[float, str]] = {}
    i = start
    while i < len(lines):
        line = lines[i].strip()
        if not line or line.startswith("#"):
            break
        tokens = line.split()
        if len(tokens) >= 2:
            val = _to_float(tokens[1])
            if val is not None:
                units = tokens[2] if len(tokens) >= 3 else ""
                out[tokens[0]] = (val, units)
        i += 1
    return out, i


def parse_stab(path: str | Path) -> Dict[str, Any]:
    """解析 .stab 文件, 返回结构化字典。

    返回结构:
    {
      "reference":   { Sref, Cref, Bref, Xcg, Ycg, Zcg, Mach, AoA, Beta, Vinf, ... }
      "perturbations": [ {case, delta, units, coefs:{CFx..CMn}} ... ]
      "control_groups": { "ConGrp_1": "MainWing_Aileron", ... }
      "derivatives": { "CL":  {"base": v, "Alpha": v, "Beta": v, ..., "ConGrp_1": v} ... }
      "results":     { "SM": (v, units), "X_np": (v, units) }
      "derivative_columns": [列名列表]
      "source_file": str
    }
    """
    p = Path(path)
    if not p.is_file():
        raise FileNotFoundError(f".stab 文件不存在: {p}")
    lines = p.read_text(encoding="utf-8", errors="replace").splitlines()

    result: Dict[str, Any] = {"source_file": str(p)}

    # ---- 1) 头部参考量块: 定位 "# Name  Value  Units" 后的内容 ----
    reference: Dict[str, float] = {}
    ref_units: Dict[str, str] = {}
    perturbations: List[Dict[str, Any]] = []
    control_groups: Dict[str, str] = {}
    derivatives: Dict[str, Dict[str, float]] = {}
    deriv_columns: List[str] = []
    results: Dict[str, Tuple[float, str]] = {}

    i = 0
    n = len(lines)
    while i < n:
        stripped = lines[i].strip()

        # 头部块
        if stripped.startswith("#") and "Name" in stripped and "Value" in stripped and "Units" in stripped \
                and "Case" not in stripped and "Result" not in stripped:
            block, _ = _parse_name_value_block(lines, i + 1)
            if block and "Sref_" in block:  # 是参考量头
                for k, (v, u) in block.items():
                    reference[k.rstrip("_")] = v
                    ref_units[k.rstrip("_")] = u
                i += 1
                continue

        # 结果块
        if stripped.startswith("#") and "Result" in stripped and "Value" in stripped:
            block, _ = _parse_name_value_block(lines, i + 1)
            results = block
            i += 1
            continue

        # 扰动表: 找 "Case ... Delta ... Units ... CFx" 表头行 (可能带 # 前缀也可能没有)
        _case_content = stripped.lstrip("#").strip()
        if _case_content.split()[:1] == ["Case"] and "Delta" in stripped and "CFx" in stripped:
            j = i + 1
            saw_data = False
            while j < n:
                row = lines[j].strip()
                if not row:
                    j += 1
                    continue
                if row.startswith("#"):
                    if saw_data:
                        break  # 表格结束
                    j += 1   # 表头后的 # 分隔行, 跳过
                    continue
                tokens = row.split()
                if len(tokens) >= 3:
                    delta = _to_float(tokens[1])
                    if delta is not None and len(tokens) >= 3 + len(PERTURB_COEF_COLS):
                        coefs = {name: _to_float(t)
                                 for name, t in zip(PERTURB_COEF_COLS, tokens[3:3 + len(PERTURB_COEF_COLS)])}
                        if all(v is not None for v in coefs.values()):
                            saw_data = True
                            perturbations.append({
                                "case": tokens[0], "delta": delta, "units": tokens[2],
                                "coefs": coefs,
                            })
                j += 1
            i = j
            continue

        # 导数表: 找以 "Coef" 开头的表头行 (可能是注释行 "# Coef ..." 或直接 "Coef ...")
        if stripped.startswith("#"):
            content = stripped.lstrip("#").strip()
        else:
            content = stripped
        if content.startswith("Coef") and "Alpha" in content and "Beta" in content:
            header_tokens = content.split()
            # 形如 [Coef, Total/Base, Alpha, Beta, p, q, r, Mach, U, ConGrp_1, ...]
            deriv_columns = header_tokens[2:]  # 去掉 Coef 与 Base 列
            j = i + 1
            saw_data = False
            while j < n:
                row = lines[j].strip()
                if not row:
                    j += 1
                    continue
                if row.startswith("#"):
                    if saw_data:
                        break  # 表格结束
                    j += 1   # 表头与数据行之间的单位注释行, 跳过
                    continue
                tokens = row.split()
                if len(tokens) >= 2 + len(deriv_columns):
                    coef_name = tokens[0]
                    vals = [_to_float(t) for t in tokens[2:2 + len(deriv_columns)]]
                    if all(v is not None for v in vals):
                        saw_data = True
                        row_dict = {"base": _to_float(tokens[1])}
                        row_dict.update(dict(zip(deriv_columns, vals)))
                        derivatives[coef_name] = row_dict
                j += 1
            i = j
            continue

        i += 1

    # ---- 控制面组映射: 扰动表中非标准工况按出现顺序对应 ConGrp_1..N ----
    grp_idx = 0
    for pert in perturbations:
        if pert["case"] not in STD_PERTURB_CASES:
            grp_idx += 1
            control_groups[f"ConGrp_{grp_idx}"] = pert["case"]

    result["reference"] = reference
    result["reference_units"] = ref_units
    result["perturbations"] = perturbations
    result["control_groups"] = control_groups
    result["derivative_columns"] = deriv_columns
    result["derivatives"] = derivatives
    result["results"] = {k: {"value": v, "units": u} for k, (v, u) in results.items()}

    if not derivatives:
        raise ValueError(
            f"未能从 {p.name} 解析出导数表 —— 文件可能不是 STABILITY_DEFAULT 模式的 .stab 输出"
        )
    return result


def main(argv: Optional[List[str]] = None) -> int:
    parser = argparse.ArgumentParser(description="解析 VSPAERO .stab 稳定性输出")
    parser.add_argument("file", help=".stab 文件路径")
    parser.add_argument("--json", action="store_true", help="以 JSON 输出")
    args = parser.parse_args(argv)

    try:
        result = parse_stab(args.file)
    except (FileNotFoundError, ValueError) as e:
        print(f"错误: {e}", file=sys.stderr)
        return 1

    if args.json:
        print(json.dumps(result, indent=2, ensure_ascii=False))
    else:
        ref = result["reference"]
        print(f"文件: {result['source_file']}")
        print(f"参考量: Sref={ref.get('Sref')} bref={ref.get('Bref')} cref={ref.get('Cref')}"
              f"  cg=({ref.get('Xcg')}, {ref.get('Ycg')}, {ref.get('Zcg')})")
        print(f"状态点: Mach={ref.get('Mach')} alpha={ref.get('AoA')} deg beta={ref.get('Beta')} deg")
        print(f"控制面组: {result['control_groups']}")
        print(f"导数列: {result['derivative_columns']}")
        print("主要导数 (base / wrt Alpha / wrt Beta / wrt p / wrt q / wrt r):")
        for coef in ("CL", "CD", "CS", "CMl", "CMm", "CMn"):
            d = result["derivatives"].get(coef, {})
            print(f"  {coef:4s} base={d.get('base', float('nan')):+.5f}"
                  f"  a={d.get('Alpha', float('nan')):+.5f}"
                  f"  b={d.get('Beta', float('nan')):+.5f}"
                  f"  p={d.get('p', float('nan')):+.5f}"
                  f"  q={d.get('q', float('nan')):+.5f}"
                  f"  r={d.get('r', float('nan')):+.5f}")
        for name, info in result["results"].items():
            print(f"结果: {name} = {info['value']} {info['units']}")
    return 0


if __name__ == "__main__":
    sys.exit(main())
