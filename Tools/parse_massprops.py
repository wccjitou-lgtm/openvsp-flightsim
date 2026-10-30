#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""
parse_massprops.py — 解析 OpenVSP Mass Properties 分析输出文件 (*_MassProps.txt)

文件格式 (OpenVSP 3.51.3 实测):
  - 顶部警告行 (开放网格合并提示, 可选)
  - "...Mass Properties..." 标题
  - "N Num Comps" / "N Total Num Meshes" / "N Total Num Tris" 摘要行
  - 总计块: 4 行 "数值... 标签" (Total Mass / Center of Gravity / Ixx,Iyy,Izz / Ixy,Ixz,Iyz / Volume)
  - 部件表: 表头 "Name Mass cgX cgY cgZ Ixx Iyy Izz Ixy Ixz Iyz Volume"
           + 每个部件一行 + 重复表头 + "Totals" 行
  - 之后可能还有 "...Filling Mass Properties..." 切片表 (本解析器忽略)

按开发提示词 3.2 节要求, 以 Totals 行为准提取:
  mass_kg, cg (x,y,z), 惯量张量 Ixx/Iyy/Izz/Ixy/Ixz/Iyz, volume

单位说明: OpenVSP GUI 默认单位制为 长度 m / 质量 kg / 惯量 kg*m^2。
若建模时改过单位偏好, 输出数值会随之变化 —— 生成 AircraftConfig.json 时
以本解析器返回的 units 字段为准记录。

用法:
  python parse_massprops.py <MassProps.txt> [--json]     # --json 输出 JSON
  python parse_massprops.py <MassProps.txt>              # 人类可读输出
"""

from __future__ import annotations

import argparse
import json
import sys
from pathlib import Path
from typing import Any, Dict, List, Optional

# MassProps 部件表的列名 (OpenVSP 3.51.3 实测)
TABLE_COLUMNS = ["Name", "Mass", "cgX", "cgY", "cgZ",
                 "Ixx", "Iyy", "Izz", "Ixy", "Ixz", "Iyz", "Volume"]

# 输出中假定的单位制 (GUI 默认)
UNITS = {"length": "m", "mass": "kg", "inertia": "kg*m^2", "angle": "deg"}


def _to_float(token: str) -> Optional[float]:
    try:
        return float(token)
    except ValueError:
        return None


def _parse_summary_block(lines: List[str]) -> Dict[str, Any]:
    """解析文件顶部 "数值 标签" 形式的总计块。"""
    summary: Dict[str, Any] = {}
    labels = {
        "Total Mass": "total_mass",
        "Center of Gravity": "cg",
        "Ixx, Iyy, Izz": "inertia_diag",
        "Ixy, Ixz, Iyz": "inertia_cross",
        "Volume": "total_volume",
    }
    for line in lines:
        # 行格式: "数值 [数值...] 标签" —— 标签在行尾
        for label, key in labels.items():
            if line.rstrip().endswith(label):
                prefix = line[: line.rfind(label)]
                vals = [_to_float(t) for t in prefix.split()]
                vals = [v for v in vals if v is not None]
                if not vals:
                    break
                if key == "cg":
                    summary["cg"] = vals[:3]
                elif key == "inertia_diag":
                    summary["inertia_diag"] = vals[:3]
                elif key == "inertia_cross":
                    summary["inertia_cross"] = vals[:3]
                elif key == "total_mass":
                    summary["total_mass"] = vals[0]
                elif key == "total_volume":
                    summary["total_volume"] = vals[0]
                break
    return summary


def _parse_component_tables(lines: List[str]) -> Dict[str, Dict[str, Any]]:
    """解析部件表, 返回 {'部件名': {...}, 'Totals': {...}}。"""
    tables: Dict[str, Dict[str, Any]] = {}
    header_idx: List[int] = []
    for i, line in enumerate(lines):
        tokens = line.split()
        if tokens and tokens[0] == "Name" and tokens == TABLE_COLUMNS:
            header_idx.append(i)

    for h in header_idx:
        # 表头后连续的数据行 (Name 列为字符串、其余为数值)。
        # 部件名可含空格 (OpenVSP 允许, 如 "main wing L"): 行尾 11 列按数值
        # 右对齐切分, 其余 token 合成名 (U7 — 原按整行 12 列切分, 空格名
        # 会使整张表在该行截断, 该件从质量表消失 → 换机烘焙回落 Density 口径)
        j = h + 1
        while j < len(lines):
            tokens = lines[j].split()
            if not tokens or _to_float(tokens[0]) is not None:
                break  # 下一张表或空行
            if tokens[0] == "Name":
                break  # 重复表头
            if len(tokens) < len(TABLE_COLUMNS):
                break
            nums = [_to_float(t) for t in tokens[-11:]]
            row_name = " ".join(tokens[:-11])
            if not row_name or any(v is None for v in nums):
                break
            row: Dict[str, Any] = dict(zip(TABLE_COLUMNS[1:], nums))
            prev = tables.get(row_name)
            if prev is not None:
                # 对称镜像片(未合并网格)以同名多行出现: 质量/体积求和, 质心按质量加权;
                # 惯量列仅为各片自身惯量之和, 不含平行轴项
                m = prev["Mass"] + row["Mass"]
                for k in ("cgX", "cgY", "cgZ"):
                    row[k] = ((prev[k] * prev["Mass"] + row[k] * row["Mass"]) / m) if m else prev[k]
                for k in TABLE_COLUMNS[5:]:
                    row[k] = prev[k] + row[k]
                row["Mass"] = m
            tables[row_name] = row
            j += 1
    return tables


def parse_massprops(path: str | Path) -> Dict[str, Any]:
    """解析 MassProps 文件, 返回结构化字典。

    返回结构:
    {
      "units": {...},
      "num_comps": int,
      "totals": { "mass_kg", "cg_m": [x,y,z], "inertia_kgm2": {...}, "volume_m3" },
      "components": { "<部件名>": {同 totals 结构, 无 volume 聚合差异} },
      "source_file": str
    }
    异常: FileNotFoundError / ValueError (缺少 Totals 行等)
    """
    p = Path(path)
    if not p.is_file():
        raise FileNotFoundError(f"MassProps 文件不存在: {p}")
    text = p.read_text(encoding="utf-8", errors="replace")
    lines = text.splitlines()

    # 摘要行: "2 Num Comps"
    num_comps: Optional[int] = None
    for line in lines:
        tokens = line.split()
        if len(tokens) == 2 and tokens[1] == "Num" and tokens[0].isdigit() and "Comps" in line:
            # 形如 "2 Num Comps"
            if line.strip().endswith("Num Comps"):
                num_comps = int(tokens[0])
                break

    summary = _parse_summary_block(lines)
    tables = _parse_component_tables(lines)

    if "Totals" not in tables:
        raise ValueError(
            f"未在 {p.name} 中找到 Totals 行 —— 文件格式可能不是 OpenVSP MassProps 输出"
        )
    tot = tables["Totals"]

    result: Dict[str, Any] = {
        "units": dict(UNITS),
        "num_comps": num_comps if num_comps is not None else len(tables) - 1,
        "totals": {
            "mass_kg": tot["Mass"],
            "cg_m": [tot["cgX"], tot["cgY"], tot["cgZ"]],
            "inertia_kgm2": {
                "Ixx": tot["Ixx"], "Iyy": tot["Iyy"], "Izz": tot["Izz"],
                "Ixy": tot["Ixy"], "Ixz": tot["Ixz"], "Iyz": tot["Iyz"],
            },
            "volume_m3": tot["Volume"],
        },
        "components": {k: v for k, v in tables.items() if k != "Totals"},
        "source_file": str(p),
        # 顶部总计块 (独立于表格, 用于交叉校验)
        "summary_block": summary,
    }
    return result


def main(argv: Optional[List[str]] = None) -> int:
    parser = argparse.ArgumentParser(description="解析 OpenVSP *_MassProps.txt")
    parser.add_argument("file", help="MassProps 文件路径")
    parser.add_argument("--json", action="store_true", help="以 JSON 输出")
    args = parser.parse_args(argv)

    try:
        result = parse_massprops(args.file)
    except (FileNotFoundError, ValueError) as e:
        print(f"错误: {e}", file=sys.stderr)
        return 1

    if args.json:
        print(json.dumps(result, indent=2, ensure_ascii=False))
    else:
        t = result["totals"]
        print(f"文件: {result['source_file']}")
        print(f"部件数: {result['num_comps']}  ({', '.join(result['components'])})")
        print(f"总质量:   {t['mass_kg']:.6g} kg")
        print(f"重心:     [{t['cg_m'][0]:.6g}, {t['cg_m'][1]:.6g}, {t['cg_m'][2]:.6g}] m")
        print(f"惯量:     Ixx={t['inertia_kgm2']['Ixx']:.6g}  Iyy={t['inertia_kgm2']['Iyy']:.6g}  "
              f"Izz={t['inertia_kgm2']['Izz']:.6g} kg*m^2")
        print(f"          Ixy={t['inertia_kgm2']['Ixy']:.6g}  Ixz={t['inertia_kgm2']['Ixz']:.6g}  "
              f"Iyz={t['inertia_kgm2']['Iyz']:.6g} kg*m^2")
        print(f"总体积:   {t['volume_m3']:.6g} m^3")
    return 0


if __name__ == "__main__":
    sys.exit(main())
