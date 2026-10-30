#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""
workshop_common.py — 机型工坊 Python 侧共享常量与工具 (W1)

被 Tools/workshop_worker.py (常驻 worker) 与 Tests/test_workshop_worker.py 复用。
仅标准库 + openvsp (不引入第三方依赖, 见提示词 §13)。
"""
from __future__ import annotations

import os
import sys
from pathlib import Path

# OpenVSP 安装目录由捆绑布局或 VSPROOT 环境变量提供。
# 六槽位 (提示词 §5.2 表格顺序); 值 = 允许的 GetGeomTypeName 类型集合
SLOTS = ["fuselage", "wing1", "wing2", "htail", "vtail", "ballast"]
SLOT_TYPES = {
    "fuselage": {"Fuselage", "Pod"},   # TestUAV 等旧机型机身即 Pod (闭合实体)
    "wing1": {"Wing"},
    "wing2": {"Wing"},
    "htail": {"Wing"},
    "vtail": {"Wing"},
    "ballast": {"Pod"},
}
SLOT_LABELS_ZH = {
    "fuselage": "机身",
    "wing1": "机翼1",
    "wing2": "机翼2",
    "htail": "平尾",
    "vtail": "垂尾",
    "ballast": "配重",
}

# 库类型目录 (提示词 §9): Data/Workshop/library/<类型>/<名称>.vsp3 + <名称>.part.json
LIBRARY_TYPES = ["fuselage", "wing", "ballast"]

# 气动集合 = 用户集 3 (SET_FIRST_USER, 与 import_vsp.py 一致)
AERO_SET = 3
# 质量集合 = 用户集 4 (与 import_vsp.py 的 CGGeomSet 完整质量集约定一致)。
# MassProp 按分析 Set 输入算质量, 不理会显示状态 → 排除语义由集 4 表达
# (集4 = 全部未排除几何; 隐藏 ⇔ 移出集3/集4)。
MASS_SET = 4
# OpenVSP 集合标志: 1 = SET_SHOWN (显示)
SET_SHOWN = 1


def vsproot() -> Path:
    """Return the bundled OpenVSP directory or the directory named by VSPROOT."""
    bundled = project_root() / "OpenVSP"
    if (bundled / "python" / "openvsp").is_dir():
        return bundled
    env = os.environ.get("VSPROOT")
    if env:
        return Path(env).expanduser()
    raise FileNotFoundError(
        "OpenVSP was not found. Set VSPROOT to the directory containing "
        "vspscript.exe and python/openvsp, or pass --openvsp-dir explicitly."
    )


def setup_openvsp(vsproot_path: Path | None = None) -> None:
    """注入 OpenVSP python 包路径 + DLL 目录 (须在 import openvsp 前调用)。

    与 AircraftWorkshop/tools/probe_api*.py 同款样板 (W0 实测)。
    """
    root = Path(vsproot_path) if vsproot_path else vsproot()
    for p in [r"\python\openvsp", r"\python\openvsp_config", r"\python\degen_geom",
              r"\python\utilities", r"\python"]:
        sys.path.insert(0, str(root) + p)
    os.add_dll_directory(str(root))


def project_root() -> Path:
    """openvsp-flightsim 仓库根 (本文件在 Tools/ 下)。"""
    return Path(__file__).resolve().parent.parent


def stl_triangle_count(path: Path) -> int:
    """ASCII STL 三角形计数 (OpenVSP 导出行首带空格 + CRLF, 必须 strip)。"""
    n = 0
    with open(path, "r", encoding="utf-8", errors="replace") as f:
        for line in f:
            if line.strip().startswith("facet normal"):
                n += 1
    return n


def ascii_name(name: str) -> str:
    """库文件落盘名安全化: 非 [A-Za-z0-9_.-] 一律换 '_' (vsp 库窄字符写盘,
    非 ASCII 文件名直接崩溃 — W0/W1 实测)。中文显示名存 .part.json 的 name 字段。"""
    import re
    s = re.sub(r"[^A-Za-z0-9_.\-]", "_", str(name).strip())
    return s or "Part"


def geom_name_safe(name: str) -> str:
    """几何名安全化: 仅保留 [A-Za-z0-9] (VSPAERO 操纵面 tag 匹配会被 '_' 和
    非 ASCII 打断 — 2026-10-07 e2e 实测 "C_Wing" 被改写成 "Cing", 不产出 polar)。
    空结果兜底 "Part"。"""
    import re
    s = re.sub(r"[^A-Za-z0-9]", "", str(name).strip())
    return s or "Part"


def geom_name_ok(name: str) -> bool:
    """几何名是否 VSPAERO 安全 (等于其消毒结果)。"""
    return geom_name_safe(name) == str(name).strip()


def scan_vsp3_geom_names(path: Path) -> list[str]:
    """从 vsp3 (XML) 抽取几何名: 每个 <Geom> 块第一个 ParmContainer 的 <Name>。
    供不动 openvsp 的驱动脚本 (workshop_aero/import_vsp) 做前置名称校验。"""
    import re
    text = Path(path).read_text(encoding="utf-8", errors="replace")
    return re.findall(
        r"<Geom>\s*<ParmContainer>\s*<ID>[^<]*</ID>\s*<Name>([^<]*)</Name>", text)


def bad_geom_names(names: list[str]) -> list[str]:
    """筛出 VSPAERO 不安全的几何名 (含 '_' 或非 ASCII)。"""
    return [n for n in names if not geom_name_ok(n)]
