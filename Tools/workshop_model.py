#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""
workshop_model.py — 机型工坊 W2 建模操作 (被 workshop_worker.py 调用)

涵盖提示词 §5 编辑窗的全部模型侧能力:
  - get_geom_summary   编辑窗初始值契约 (机翼/机身/配重 三类)
  - add_part           新建默认部件到槽位 (含槽位默认装配)
  - set_parms          几何级/XSec 级批量改参 + Update
  - fuselage_set_radii 机身半径列表 (圆形化+等分+分布趋势插值重采样)
  - set_mass           质量 (机身 Density=方案A / 翼·配重 PointMass)
  - set_airfoil        翼型 (NACA4 参数化 / 文件翼型)
  - validate_airfoil   上传 .dat 校验 (Selig/Lednicer 点序)
  - add/edit/del_control  操纵面 (SS_Control, 功能类型决定最终名称)
  - wing_to_single_taper  多段机翼 → 单段梯形
  - load_example       §10 示例机
所有 parm 名来自 W0 运行时反查 (docs/workshop_param_map.md)。
"""
from __future__ import annotations

import math
import re
import shutil
from pathlib import Path

import workshop_common as wc

import openvsp as vsp

# 功能类型 → 子面最终名称 (提示词铁律 5: 模拟器按名识别三轴)
CONTROL_ROLES = {"aileron": "Aileron", "elevator": "Elevator", "rudder": "Rudder"}


def _surf(geom_id: str) -> str:
    return vsp.GetXSecSurf(geom_id, 0)


def _num_xsecs(geom_id: str) -> int:
    return int(vsp.GetNumXSec(_surf(geom_id)))


def _set(geom_id: str, name: str, group: str, value: float) -> None:
    vsp.SetParmValUpdate(geom_id, name, group, value)


def set_position(geom_id: str, x: float, y: float, z: float) -> None:
    """写绝对位置。默认 Abs_Or_Relitive_flag=1 (相对模式) 下 X_Location 是
    派生只读量 (W2 实测: 写 X_Location 静默无效), 必须先切绝对模式。"""
    vsp.SetParmValUpdate(geom_id, "Abs_Or_Relitive_flag", "XForm", 0.0)
    _set(geom_id, "X_Location", "XForm", x)
    _set(geom_id, "Y_Location", "XForm", y)
    _set(geom_id, "Z_Location", "XForm", z)


def set_rotation(geom_id: str, rx: float, ry: float, rz: float) -> None:
    """写绝对旋转 (审查 R05): Abs_Or_Relitive_flag 同时管控位置与旋转 —
    绝对模式下 *_Rel_Rotation 派生只读, 必须写 X/Y/Z_Rotation (探针实测
    flag=0 后 X_Rotation=90 写入读回且 vsp3 往返保持)。"""
    vsp.SetParmValUpdate(geom_id, "Abs_Or_Relitive_flag", "XForm", 0.0)
    _set(geom_id, "X_Rotation", "XForm", rx)
    _set(geom_id, "Y_Rotation", "XForm", ry)
    _set(geom_id, "Z_Rotation", "XForm", rz)


def _get(geom_id: str, name: str, group: str) -> float:
    return vsp.GetParmVal(geom_id, name, group)


def wing_total_span(geom_id: str) -> float:
    """机翼总半展 = 各段 Span 之和 (与 wing_to_single_taper 同口径)。
    单段机翼即 XSec_1 半展; delete_hidden 的 PM CGy 展开用它 (单段口径
    对多段机翼会低估 Ixx)。"""
    n = _num_xsecs(geom_id)
    return sum(_get(geom_id, "Span", f"XSec_{i}") for i in range(1, max(2, n)))


# ============================ get_geom_summary ============================

def get_geom_summary(geom_id: str) -> dict:
    """编辑窗契约: 按几何类型返回全部可编辑参数当前值。"""
    gtype = vsp.GetGeomTypeName(geom_id)
    out = {
        "id": geom_id,
        "name": vsp.GetGeomName(geom_id),
        "type": gtype,
        "material": vsp.GetGeomMaterialName(geom_id),
        "x": _get(geom_id, "X_Location", "XForm"),
        "y": _get(geom_id, "Y_Location", "XForm"),
        "z": _get(geom_id, "Z_Location", "XForm"),
        "rot_x": _get(geom_id, "X_Rotation", "XForm"),      # R05: 绝对姿态
        "rot_y": _get(geom_id, "Y_Rotation", "XForm"),
        "rot_z": _get(geom_id, "Z_Rotation", "XForm"),
    }
    if gtype == "Wing":
        n = _num_xsecs(geom_id)
        out.update({
            "num_xsecs": n,
            "multi_segment": n > 2,
            "span_half": _get(geom_id, "Span", "XSec_1"),
            "root_chord": _get(geom_id, "Root_Chord", "XSec_1"),
            "tip_chord": _get(geom_id, "Tip_Chord", "XSec_1"),
            "sweep": _get(geom_id, "Sweep", "XSec_1"),
            "sweep_loc": _get(geom_id, "Sweep_Location", "XSec_1"),
            "twist": _get(geom_id, "Twist", "XSec_1"),
            "dihedral": _get(geom_id, "Dihedral", "XSec_1"),
            "symmetric": vsp.GetParmVal(vsp.FindParm(geom_id, "Sym_Planar_Flag", "Sym")) > 0.5,
            "point_mass": _get(geom_id, "PointMass", "Mass_Props"),
            "airfoil": _airfoil_state(geom_id),
            "controls": _controls_list(geom_id),
        })
    elif gtype == "Fuselage":
        surf = _surf(geom_id)
        n = _num_xsecs(geom_id)
        shapes, diameters, xlocs, round_ok = [], [], [], True
        for i in range(n):
            xs = vsp.GetXSec(surf, i)
            shape = int(vsp.GetXSecShape(xs))
            shapes.append(shape)
            if shape == vsp.XS_POINT:
                diameters.append(0.0)
            elif shape == vsp.XS_CIRCLE:
                p = vsp.GetXSecParm(xs, "Circle_Diameter")
                diameters.append(vsp.GetParmVal(p) if p else None)
            elif shape == vsp.XS_ELLIPSE:
                ph = vsp.GetXSecParm(xs, "Ellipse_Height")
                pw = vsp.GetXSecParm(xs, "Ellipse_Width")
                h = vsp.GetParmVal(ph) if ph else 0.0
                w = vsp.GetParmVal(pw) if pw else 0.0
                diameters.append(round((h + w) / 2.0, 6) if h > 0 and w > 0 else None)
            else:
                diameters.append(None)
                round_ok = False
            pl = vsp.GetXSecParm(xs, "XLocPercent")
            xlocs.append(vsp.GetParmVal(pl) if pl else i / max(1, n - 1))
        out.update({
            "length": _get(geom_id, "Length", "Design"),
            "num_xsecs": n,
            "shapes": shapes,
            "all_round_or_ellipse": round_ok,
            "diameters": diameters,
            "xloc_percents": xlocs,
            "tail_cap": int(_get(geom_id, "CapUMaxOption", "EndCap")),
            "density": _get(geom_id, "Density", "Mass_Props"),
        })
    elif gtype == "Pod":
        out.update({
            "length": _get(geom_id, "Length", "Design"),
            "point_mass": _get(geom_id, "PointMass", "Mass_Props"),
            "mass_density": _get(geom_id, "Density", "Mass_Props"),
            "cgx": _get(geom_id, "CGx", "Mass_Props"),
            "cgy": _get(geom_id, "CGy", "Mass_Props"),
            "cgz": _get(geom_id, "CGz", "Mass_Props"),
        })
    return out


def _airfoil_state(geom_id: str) -> dict:
    xs = vsp.GetXSec(_surf(geom_id), 1)
    shape = int(vsp.GetXSecShape(xs))

    def xparm(name: str) -> float:
        p = vsp.GetXSecParm(xs, name)
        return vsp.GetParmVal(p) if p else 0.0

    if shape == vsp.XS_FOUR_SERIES:
        return {"kind": "naca4", "thick": xparm("ThickChord"), "camber": xparm("Camber"),
                "camber_loc": xparm("CamberLoc")}
    if shape == vsp.XS_FILE_AIRFOIL:
        return {"kind": "file", "file": ""}   # 文件名 OpenVSP 不回读, 由工坊侧记录
    return {"kind": "other", "shape": shape}


def _controls_list(geom_id: str) -> list[dict]:
    out = []
    for ss in vsp.GetSubSurfIDVec(geom_id):
        if int(vsp.GetSubSurfType(ss)) != vsp.SS_CONTROL:
            continue
        # 枚举法取 parm (保存重开后 (id,name,group) 三元寻址不可靠, W3 实测)
        sv = {vsp.GetParmName(p): vsp.GetParmVal(p) for p in vsp.GetSubSurfParmIDs(ss)}
        out.append({
            "id": ss,
            "name": vsp.GetSubSurfName(geom_id, ss),
            "eta_start": sv.get("EtaStart", 0.0),
            "eta_end": sv.get("EtaEnd", 0.0),
            "len_c": sv.get("Length_C_Start", 0.0),
        })
    return out


# ============================ add_part ============================

# 槽位默认装配 (提示词 §5.2 规则 6: 仅全新默认部件套用; 位置 m, 机身长按 1.6)
SLOT_DEFAULTS = {
    "fuselage": {},
    "wing1": {"x": 0.70, "y": 0.0, "z": 0.0},
    "wing2": {"x": 0.70, "y": 0.0, "z": 0.06},
    "htail": {"x": 1.45, "y": 0.0, "z": 0.08},
    "vtail": {"x": 1.35, "y": 0.0, "z": 0.10, "rot_x": 90.0, "symmetric": False},
    "ballast": {"x": 0.10, "y": 0.0, "z": 0.0},
}


def add_part(slot: str, name: str | None = None) -> str:
    """新建默认部件 (未入槽, 由 worker 命令层 assign); 返回 geom_id。"""
    if slot == "fuselage":
        gid = vsp.AddGeom("FUSELAGE", "")
        if name:
            vsp.SetGeomName(gid, wc.geom_name_safe(name))   # F1: VSPAERO tag 安全
        _set(gid, "Length", "Design", 1.6)
        fuselage_set_radii(gid, [0.0, 0.15, 0.19, 0.19, 0.15, 0.08])   # 截面0=点, sin 型流线
        _set(gid, "Density", "Mass_Props", 5.0)
    elif slot in ("wing1", "wing2", "htail", "vtail"):
        gid = vsp.AddGeom("WING", "")
        if name:
            vsp.SetGeomName(gid, wc.geom_name_safe(name))
        if slot in ("wing1", "wing2"):
            _set(gid, "Span", "XSec_1", 1.0)          # 半展 (全展 2.0)
            _set(gid, "Root_Chord", "XSec_1", 0.25)
            _set(gid, "Tip_Chord", "XSec_1", 0.15)
        else:
            _set(gid, "Span", "XSec_1", 0.35 if slot == "htail" else 0.30)
            _set(gid, "Root_Chord", "XSec_1", 0.12)
            _set(gid, "Tip_Chord", "XSec_1", 0.08)
        # 壳体面密度质量: 质量沿翼面分布, 重心/转动惯量随几何自洽 (PointMass 会把
        # 惯量集中在翼根中心线, Ixx 失真); 1.5 kg/m2 与旧默认 (面积×1.5) 总质量相当
        _set(gid, "Shell_Flag", "Mass_Props", 1.0)
        _set(gid, "Mass_Area", "Mass_Props", 1.5)
        _set(gid, "Density", "Mass_Props", 1.0)
        _set(gid, "PointMass", "Mass_Props", 0.0)
        d = SLOT_DEFAULTS[slot]
        set_position(gid, d["x"], d["y"], d["z"])
        if "rot_x" in d:
            set_rotation(gid, d["rot_x"], 0.0, 0.0)   # R05: 绝对旋转
        set_symmetric(gid, d.get("symmetric", True))
        if slot == "vtail":
            pass   # 单垂尾; 对称+间距在编辑窗切换
    elif slot == "ballast":
        gid = vsp.AddGeom("POD", "")
        if name:
            vsp.SetGeomName(gid, wc.geom_name_safe(name))
        _set(gid, "Length", "Design", 0.05)
        _set(gid, "Density", "Mass_Props", 0.0)
        _set(gid, "PointMass", "Mass_Props", 0.2)
        set_symmetric(gid, False)   # 配重禁止对称 (点质量会镜像双计, W2 实测)
        d = SLOT_DEFAULTS[slot]
        set_position(gid, d["x"], d["y"], d["z"])
    else:
        raise ValueError(f"未知槽位: {slot}")
    vsp.SetSetFlag(gid, wc.MASS_SET, True)
    vsp.Update()
    return gid


def set_symmetric(geom_id: str, on: bool) -> None:
    p = vsp.FindParm(geom_id, "Sym_Planar_Flag", "Sym")
    if not p:
        return
    # 默认值 2 = XZ 平面 (左右镜像); 关闭 = 0 (SYM_NONE) — W0 反查枚举
    vsp.SetParmValUpdate(p, 2.0 if on else 0.0)


# ============================ 机身半径 ============================

def fuselage_set_radii(geom_id: str, diameters: list[float]) -> None:
    """机身截面直径列表 (圆形化 + 等分)。

    提示词 §5.4: 一旦编辑截面数/半径, 全部截面转圆 (保形语义由调用方在
    转圆前提示); 截面沿长度等分; 数量变化时保留分布趋势 (调用方先重采样)。
    """
    n = len(diameters)
    if n < 2:
        raise ValueError("机身至少需要 2 个截面")
    cur = _num_xsecs(geom_id)
    surf = _surf(geom_id)
    # 调整截面数量 (增: 在倒数第二段前插 — 末尾 index 插入静默无效, W2 实测; 减: 从中间抽)
    while cur < n:
        vsp.InsertXSec(geom_id, cur - 2, vsp.XS_CIRCLE)
        if _num_xsecs(geom_id) == cur:   # 防御: 插入未生效避免死循环
            raise RuntimeError(f"InsertXSec 未生效 (截面数仍 {cur})")
        cur += 1
    while cur > n:
        vsp.CutXSec(geom_id, cur // 2)
        cur -= 1
    for i in range(n):
        # 截面 0 固定为点 (机头闭合, diameters[0] 忽略); 其余为圆
        vsp.ChangeXSecShape(surf, i, vsp.XS_POINT if i == 0 else vsp.XS_CIRCLE)
        xs = vsp.GetXSec(surf, i)
        if i > 0:
            p = vsp.GetXSecParm(xs, "Circle_Diameter")
            if p:
                vsp.SetParmVal(p, max(1e-4, diameters[i]))
        pl = vsp.GetXSecParm(xs, "XLocPercent")
        if pl:
            vsp.SetParmVal(pl, i / (n - 1))
    # 尾部截面封盖: 平面封盖 (U 最大端)
    _set(geom_id, "CapUMaxOption", "EndCap", float(vsp.FLAT_END_CAP))
    vsp.Update()


def resample_radii(old_radii: list[float], new_n: int) -> list[float]:
    """改截面数时保留分布趋势: 对旧直径曲线线性插值重采样 (端点保持)。"""
    m = len(old_radii)
    if m == 0:
        return [0.1] * new_n
    if m == 1:
        return [old_radii[0]] * new_n
    out = []
    for i in range(new_n):
        t = i / (new_n - 1) if new_n > 1 else 0.0
        x = t * (m - 1)
        j = min(int(math.floor(x)), m - 2)
        f = x - j
        out.append(round(old_radii[j] * (1 - f) + old_radii[j + 1] * f, 6))
    return out


# ============================ 质量 ============================

def set_mass(geom_id: str, mass_kg: float, last_volume_m3: float | None,
             last_wetted_area_m2: float | None = None) -> dict:
    """机身 Density=质量/体积 (方案A); 翼 Mass_Area=质量/浸润面积 (壳体, 含全部对称片);
    配重 PointMass。

    体积/面积来自最近一次 mass_props (worker 侧缓存传入); 无缓存时报错要求先跑一次质量计算。
    """
    gtype = vsp.GetGeomTypeName(geom_id)
    if gtype == "Wing":
        if not last_wetted_area_m2 or last_wetted_area_m2 <= 1e-9:
            return {"ok": False, "error": "机翼面积未知, 请先在工坊刷新一次质量/预览"}
        _set(geom_id, "Shell_Flag", "Mass_Props", 1.0)
        _set(geom_id, "PointMass", "Mass_Props", 0.0)
        _set(geom_id, "Mass_Area", "Mass_Props", mass_kg / last_wetted_area_m2)
        vsp.Update()
        return {"ok": True, "mass_area": mass_kg / last_wetted_area_m2}
    if gtype == "Fuselage":
        if not last_volume_m3 or last_volume_m3 <= 1e-9:
            return {"ok": False, "error": "机身体积未知, 请先在工坊刷新一次质量/预览"}
        _set(geom_id, "Density", "Mass_Props", mass_kg / last_volume_m3)
        vsp.Update()
        return {"ok": True, "density": mass_kg / last_volume_m3}
    _set(geom_id, "PointMass", "Mass_Props", mass_kg)
    _set(geom_id, "Density", "Mass_Props", 0.0 if gtype == "Pod" else 1.0)
    vsp.Update()
    return {"ok": True}


# ============================ 翼型 ============================

def set_airfoil(geom_id: str, kind: str, thick: float = 0.10, camber: float = 0.0,
                camber_loc: float = 0.2, file: str = "") -> None:
    surf = _surf(geom_id)
    for i in (0, 1):   # 根/尖共用一个设定 (提示词 §5.5)
        if kind == "naca4":
            vsp.ChangeXSecShape(surf, i, vsp.XS_FOUR_SERIES)
            xs = vsp.GetXSec(surf, i)
            for nm, v in (("ThickChord", thick), ("Camber", camber), ("CamberLoc", camber_loc)):
                p = vsp.GetXSecParm(xs, nm)
                if p:
                    vsp.SetParmVal(p, v)
        elif kind == "file":
            if not file:
                raise ValueError("文件翼型缺少路径")
            vsp.ChangeXSecShape(surf, i, vsp.XS_FILE_AIRFOIL)
            xs = vsp.GetXSec(surf, i)
            vsp.ReadFileAirfoil(xs, str(file))   # 返回 None, 失败通常抛异常/无效果
        else:
            raise ValueError(f"未知翼型类型: {kind}")
    vsp.Update()


def list_airfoils() -> list[dict]:
    """翼型库: <VSPROOT>\\airfoil + Data/Workshop/airfoils + NACA4 生成项。"""
    items = []
    for base, tag in [(wc.vsproot() / "airfoil", "openvsp"),
                      (wc.project_root() / "Data" / "Workshop" / "airfoils", "user")]:
        if not base.is_dir():
            continue
        for f in sorted(base.iterdir()):
            if f.suffix.lower() in (".dat", ".af"):
                items.append({"kind": "file", "name": f.stem, "path": str(f), "source": tag})
    items.append({"kind": "naca4", "name": "NACA 0012", "thick": 0.12,
                  "camber": 0.0, "camber_loc": 0.2, "path": "", "source": "gen"})
    items.append({"kind": "naca4", "name": "NACA 2412", "thick": 0.12,
                  "camber": 0.02, "camber_loc": 0.4, "path": "", "source": "gen"})
    items.append({"kind": "naca4", "name": "NACA 4412", "thick": 0.12,
                  "camber": 0.04, "camber_loc": 0.4, "path": "", "source": "gen"})
    items.append({"kind": "naca4", "name": "NACA 0006", "thick": 0.06,
                  "camber": 0.0, "camber_loc": 0.2, "path": "", "source": "gen"})
    return items


def validate_airfoil(src: Path) -> tuple[bool, str]:
    """上传 .dat 校验: 文本可读、≥10 个坐标点、x∈[-0.05,1.05]、y∈[-0.6,0.6]。

    兼容 Selig (x 从 TE 上表面 → LE → 下表面) 与 Lednicer (两个点数行);
    本校验只做结构合法性, 点序语义交给 OpenVSP ReadFileAirfoil。
    """
    if not src.is_file():
        return False, f"文件不存在: {src}"
    if src.suffix.lower() not in (".dat", ".txt"):
        return False, "仅支持 .dat / .txt 文本翼型文件"
    try:
        text = src.read_text(encoding="utf-8", errors="replace")
    except OSError as e:
        return False, f"读取失败: {e}"
    pts = []
    for line in text.splitlines()[1:]:   # 首行通常是翼型名
        tok = line.split()
        if len(tok) < 2:
            continue
        try:
            x, y = float(tok[0]), float(tok[1])
        except ValueError:
            continue
        if -0.05 <= x <= 1.05 and -0.6 <= y <= 0.6:
            pts.append((x, y))
    if len(pts) < 10:
        return False, f"有效坐标点不足 ({len(pts)} < 10), 请确认 Selig/Lednicer 点序格式"
    xs = [p[0] for p in pts]
    if max(xs) < 0.9 or min(xs) > 0.05:
        return False, "x 坐标范围异常 (应覆盖 0~1 弦长)"
    return True, f"{len(pts)} 点"


# ============================ 操纵面 ============================

def add_control(geom_id: str, role: str, eta_start: float, eta_end: float,
                len_c: float, user_name: str = "") -> dict:
    eta_start = min(max(eta_start, 0.0), 1.0)
    eta_end = min(max(eta_end, eta_start + 1e-3), 1.0)
    len_c = min(max(len_c, 0.01), 0.95)
    name = CONTROL_ROLES.get(role, None)
    if name is None:
        if role != "flap":
            raise ValueError(f"未知功能类型: {role}")
        name = user_name.strip() or "Flap"
    existing = {c["name"] for c in _controls_list(geom_id)}
    ss = vsp.AddSubSurf(geom_id, vsp.SS_CONTROL)
    _set(ss, "EtaFlag", "SS_Control", 1)
    _set(ss, "EtaStart", "SS_Control", eta_start)
    _set(ss, "EtaEnd", "SS_Control", eta_end)
    _set(ss, "Length_C_Start", "SS_Control", len_c)
    _set(ss, "Length_C_End", "SS_Control", len_c)
    vsp.SetSubSurfName(geom_id, ss, name)
    vsp.Update()
    return {"id": ss, "name": name, "eta_start": eta_start, "eta_end": eta_end,
            "len_c": len_c, "role": role}


def edit_control(geom_id: str, subsurf_id: str, eta_start: float, eta_end: float,
                 len_c: float, role: str, user_name: str = "") -> dict:
    cur = {c["id"]: c for c in _controls_list(geom_id)}
    if subsurf_id not in cur:
        raise ValueError(f"未知操纵面: {subsurf_id}")
    eta_start = min(max(eta_start, 0.0), 1.0)
    eta_end = min(max(eta_end, eta_start + 1e-3), 1.0)
    len_c = min(max(len_c, 0.01), 0.95)
    _set(subsurf_id, "EtaStart", "SS_Control", eta_start)
    _set(subsurf_id, "EtaEnd", "SS_Control", eta_end)
    _set(subsurf_id, "Length_C_Start", "SS_Control", len_c)
    _set(subsurf_id, "Length_C_End", "SS_Control", len_c)
    name = CONTROL_ROLES.get(role)
    if name is None:
        if role != "flap":
            raise ValueError(f"未知功能类型: {role}")
        name = user_name.strip() or "Flap"
    vsp.SetSubSurfName(geom_id, subsurf_id, name)
    vsp.Update()
    return {"id": subsurf_id, "name": name, "eta_start": eta_start,
            "eta_end": eta_end, "len_c": len_c, "role": role}


def del_control(geom_id: str, subsurf_id: str) -> None:
    vsp.DeleteSubSurf(geom_id, subsurf_id)
    vsp.Update()


# ============================ 多段 → 单段梯形 ============================

def wing_to_single_taper(geom_id: str) -> dict:
    """按总展长/根弦/梢弦重建单段梯形 (提示词 §5.5, 不可撤销 — UI 先确认)。"""
    n = _num_xsecs(geom_id)
    if n <= 2:
        return {"changed": False, "reason": "已是单段梯形"}
    # 总半展 = 各段 Span 之和; 根弦 = 首段 Root_Chord; 梢弦 = 末段 Tip_Chord
    total_span = sum(_get(geom_id, "Span", f"XSec_{i}") for i in range(1, n))
    root_c = _get(geom_id, "Root_Chord", "XSec_1")
    tip_c = _get(geom_id, "Tip_Chord", f"XSec_{n - 1}")
    gtype_pos = {"x": _get(geom_id, "X_Location", "XForm"),
                 "y": _get(geom_id, "Y_Location", "XForm"),
                 "z": _get(geom_id, "Z_Location", "XForm"),
                 "rot_x": _get(geom_id, "X_Rel_Rotation", "XForm"),
                 "rot_y": _get(geom_id, "Y_Rel_Rotation", "XForm"),
                 "rot_z": _get(geom_id, "Z_Rel_Rotation", "XForm")}
    name = vsp.GetGeomName(geom_id)
    controls = _controls_list(geom_id)
    mass_parms = {k: _get(geom_id, k, "Mass_Props")
                  for k in ("Shell_Flag", "Mass_Area", "Density", "PointMass")}
    mat = vsp.GetGeomMaterialName(geom_id)
    vsp.DeleteGeom(geom_id)
    gid = vsp.AddGeom("WING", "")
    vsp.SetGeomName(gid, name)
    _set(gid, "Span", "XSec_1", total_span)
    _set(gid, "Root_Chord", "XSec_1", root_c)
    _set(gid, "Tip_Chord", "XSec_1", tip_c)
    set_position(gid, gtype_pos["x"], gtype_pos["y"], gtype_pos["z"])
    set_rotation(gid, gtype_pos["rot_x"], gtype_pos["rot_y"], gtype_pos["rot_z"])
    for k, v in mass_parms.items():
        _set(gid, k, "Mass_Props", v)
    if mat:
        vsp.SetGeomMaterialName(gid, mat)
    vsp.SetSetFlag(gid, wc.MASS_SET, True)
    vsp.Update()
    return {"changed": True, "new_id": gid, "span_half": total_span,
            "root_chord": root_c, "tip_chord": tip_c,
            "controls_lost": len(controls)}


# ============================ 示例机 (§10) ============================

def load_example() -> dict:
    """示例机: 机身 1.6m/6 截面; 主翼全展 2.0; 平尾 0.7; 垂尾高 0.45; 配重 0.2kg 机头。"""
    vsp.ClearVSPModel()
    fus = add_part("fuselage", "ExampleFuselage")
    wl = add_part("wing1", "ExampleWing")
    _set(wl, "Span", "XSec_1", 1.0)             # 全展 2.0
    _set(wl, "Root_Chord", "XSec_1", 0.25)
    _set(wl, "Tip_Chord", "XSec_1", 0.15)
    add_control(wl, "aileron", 0.55, 0.95, 0.25)
    ht = add_part("htail", "ExampleHTail")
    _set(ht, "Span", "XSec_1", 0.22)            # 全展 0.44 (W4: Vbar 调常规)
    _set(ht, "Root_Chord", "XSec_1", 0.12)
    _set(ht, "Tip_Chord", "XSec_1", 0.08)
    add_control(ht, "elevator", 0.10, 0.95, 0.35)
    vt = add_part("vtail", "ExampleVTail")
    _set(vt, "Span", "XSec_1", 0.45)            # 垂尾容量 Vv≈0.06: 航向静稳定 (Cnβ>0)
    _set(vt, "Root_Chord", "XSec_1", 0.18)
    _set(vt, "Tip_Chord", "XSec_1", 0.12)
    add_control(vt, "rudder", 0.10, 0.95, 0.35)
    bal = add_part("ballast", "ExampleBallast")
    _set(bal, "PointMass", "Mass_Props", 0.5)
    _set(bal, "X_Location", "XForm", 0.53)   # 配重位置调静稳定裕度到 ~15% MAC (0.40 时 SM≈31%, 升降舵难以抬头)
    _set(bal, "CGx", "Mass_Props", 0.0)
    vsp.Update()
    return {"fuselage": fus, "wing1": wl, "htail": ht, "vtail": vt, "ballast": bal}
