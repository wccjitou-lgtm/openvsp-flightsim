#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""
workshop_aero.py — 机型工坊栏②「运行计算」(W3): 单次 VSPAEROSweep, 独立 vspscript 进程。

铁律 (import_vsp.py 同源): 每次计算一个独立 vspscript 进程; 结束后立即改名保全输出
(VSPAEROSweep 会清上一轮结果); MassProp/ComputeGeometry 后几何显示状态恢复。

用法:
  python Tools/workshop_aero.py --vsp <file.vsp3> --panel <panel.json> \
      [--work <dir>] [--openvsp-dir <含vspscript.exe的目录>]

panel.json (Unity 栏② 全部字段, 缺省用参考图默认):
  thin_set: "shown"|"none"|3      thick_set: "none"|"shown"|3
  symmetry: bool                  xz 每个布尔/数值见 _DEFAULTS
  alpha: [start,end,npts]  beta/mach/recref 同
  ref_mode: "manual"|"model"      ref_wing_id: "<几何id>"  (取自模型时)
  sref/bref/cref/xref/yref/zref: float
  wake: {fixed, iter, nodes, relax, freeze, implicit_start}
  cull: {flag, frac}              ncpu/fem2d/tecplot: int/bool
  controls: [{"name": "Elevator", "deflection_deg": 0.0}, ...]  (按名寻组)

进度行: [stage] i/N label / [ok] / [fail] / [line] <vspaero 原始输出> /
        [done] <aero_result.json 路径>   (Unity 侧行缓冲解析)
"""
from __future__ import annotations

import argparse
import json
import math
import re
import shutil
import subprocess
import sys
import time
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parent))
from generate_aircraft_config import parse_polar_raw  # noqa: E402  (原始列+行)
import workshop_common as wc  # noqa: E402


def parse_polar_full(path: Path) -> list[dict]:
    """全列解析 .polar (parse_polar 无 Cm 列, 此处取 AoA/CL/CD/Cm/L-D 并去重)。

    列 (VSPAERO 7.2.2): Beta Mach AoA Re/1e6 ... CLtot CDtot ... CMytot ... L/D
    """
    columns, rows = parse_polar_raw(path)

    def col(name):
        try:
            return columns.index(name)
        except ValueError:
            return None

    idx = {k: col(k) for k in ("Mach", "AoA", "Beta", "CLtot", "CDtot", "CMytot", "L/D")}
    if idx["AoA"] is None or idx["CLtot"] is None or idx["CDtot"] is None:
        raise ValueError(f"{path.name} 缺少必要列 AoA/CLtot/CDtot")
    cases: dict[tuple, dict] = {}
    for toks in rows:
        try:
            f = [float(t) for t in toks]
        except ValueError:
            continue
        if len(f) < len(columns):
            continue

        def g(key):
            return f[idx[key]] if idx[key] is not None else float("nan")

        key = (g("Mach"), g("AoA"), g("Beta"))
        cases[key] = {"mach": g("Mach"), "aoa_deg": g("AoA"), "beta_deg": g("Beta"),
                      "CL": g("CLtot"), "CD": g("CDtot"), "Cm": g("CMytot"),
                      "LD": g("L/D")}
    return sorted(cases.values(), key=lambda c: (c["beta_deg"], c["aoa_deg"]))

ROOT = wc.project_root()
BUNDLED = ROOT / "OpenVSP"
DEFAULT_OPENVSP = BUNDLED if (BUNDLED / "vspscript.exe").is_file() else None

# 参考图默认值 (提示词 §6; 空工程口径)
_DEFAULTS = {
    "thin_set": "shown", "thick_set": "none", "symmetry": False,
    "mode": "no_mass",
    "cull": {"flag": True, "frac": 0.030},
    "ncpu": 4, "fem2d": False, "tecplot": False,
    "wake": {"fixed": False, "iter": 3, "nodes": 8, "relax": 1.0,
             "freeze": 10000, "implicit_start": 0},
    "alpha": [0.0, 10.0, 3], "beta": [0.0, 0.0, 1], "mach": [0.0, 0.0, 1],
    "recref": [1e7, 2e7, 1],
    "ref_mode": "manual", "ref_choice": "stot", "ref_wing_id": "",
    "sref": 100.0, "bref": 1.0, "cref": 1.0,
    "xref": 10.105, "yref": 0.0, "zref": 0.0,
    "controls": [],
}

# AngelScript 模板: {{TOKEN}} 烘焙; 单进程做 集合→舵偏/增益→ComputeGeometry→Sweep
AS_TEMPLATE = r'''
// workshop_aero 烘焙脚本 (勿手改)
const string VSP3 = "{{VSP3}}";      // work 内规整副本 (绝对 ASCII 路径)
const int   ASET = {{AERO_SET}};     // 气动集 (用户集3)

// 舵面名 → 偏角 (烘焙注入); 同名多片全部生效
array<string> CtlNames;
array<double> CtlDegs;
void LoadCtlTable()
{
{{CTL_TABLE}}
}

// 铁律 3 + 审查 R08: 组寻址用带 _N 的显示组名 (GetParmGroupName 对所有组返回相同
// "ControlSurfaceGroup", 当唯一键会导致偏角互相覆盖 — import_vsp.py 同款 FindParm 法)
void FixGainsAndDeflections()
{
    string cont = FindContainer( "VSPAEROSettings", 0 );
    if ( cont.size() == 0 ) return;

    // 收集 SS_Control 子面 (id → 名字, 几何顺序) 与同名计数
    array<string> gids = FindGeoms();
    array<string> subIds;  array<string> subNames;
    array<int>     subCount;
    for ( uint g = 0; g < gids.size(); g++ )
    {
        array<string> ss = GetSubSurfIDVec( gids[g] );
        for ( uint k = 0; k < ss.size(); k++ )
        {
            if ( GetSubSurfType( ss[k] ) != SS_CONTROL ) continue;
            string nm = GetSubSurfName( gids[g], ss[k] );
            int seen = 0;
            for ( uint q = 0; q < subNames.size(); q++ )
                if ( subNames[q] == nm ) seen++;
            subIds.insertLast( ss[k] );
            subNames.insertLast( nm );
            subCount.insertLast( seen );
        }
    }

    int ngrp = GetNumControlSurfaceGroups();
    for ( int gi = 0; gi < ngrp; gi++ )
    {
        string grpNm = "ControlSurfaceGroup_" + gi;
        string defl = FindParm( cont, "DeflectionAngle", grpNm );
        if ( defl.size() == 0 ) continue;
        for ( uint s = 0; s < subIds.size(); s++ )
        {
            string p0 = FindParm( cont, "Surf_" + subIds[s] + "_0_Gain", grpNm );
            string p1 = FindParm( cont, "Surf_" + subIds[s] + "_1_Gain", grpNm );
            if ( p0.size() == 0 && p1.size() == 0 ) continue;   // 该子面不在此组

            if ( p1.size() > 0 && subNames[s] == "Elevator" )
                SetParmValUpdate( p1, -1.0 );                    // 对称偏转 (铁律 3)
            if ( p0.size() > 0 && subNames[s] == "Aileron" && subCount[s] > 0 )
                SetParmValUpdate( p0, -1.0 );                    // 第 2 片起反对称

            for ( uint m = 0; m < CtlNames.size(); m++ )
                if ( CtlNames[m] == subNames[s] )
                    SetParmValUpdate( defl, CtlDegs[m] );        // 面板偏角 (名字匹配)
        }
    }
}

void main()
{
    Print( "=== workshop_aero ===\n" );
    ReadVSPFile( VSP3 );          // 返回 void, 成败以几何数判断 (R4 教训)
    SetVSP3FileName( VSP3 );
    array<string> ids = FindGeoms();
    if ( ids.size() == 0 ) { Print( "[AERO_FAIL] empty model\n" ); return; }
    for ( uint i = 0; i < ids.size(); i++ )
        SetSetFlag( ids[i], 1, GetSetFlag( ids[i], ASET ) );  // 只显示气动集
    AutoGroupVSPAEROControlSurfaces();
    LoadCtlTable();
    FixGainsAndDeflections();
    Update();

    // ---- 参考长度/面积 ----
    SetAnalysisInputDefaults( "VSPAEROSweep" );
{{REF_BODY}}
{{CG_BODY}}

    // ---- 尾迹/求解器 ----
{{SOLVER_BODY}}

    // ---- 来流扫描 ----
{{SWEEP_BODY}}
    Update();

    // ---- 几何预处理 (剔除孤立面片在 ComputeGeometry) ----
    SetAnalysisInputDefaults( "VSPAEROComputeGeometry" );
{{COMPUTE_BODY}}
    ExecAnalysis( "VSPAEROComputeGeometry" );
    Print( "[AERO_STAGE] compute done\n" );
    ExecAnalysis( "VSPAEROSweep" );
    Print( "[AERO_STAGE] sweep done\n" );
}
'''


def _f(v) -> str:
    return repr(float(v))


def render_case(vsp3_path: Path, panel: dict) -> str:
    """把面板参数烘焙进 AngelScript。"""
    aero_set = wc.AERO_SET
    thin = panel["thin_set"]
    thick = panel["thick_set"]

    ctl_lines = []
    for c in panel.get("controls") or []:
        nm = c["name"]
        deg = float(c.get("deflection_deg", 0.0))
        ctl_lines.append(f'    CtlNames.insertLast( {json.dumps(nm, ensure_ascii=False)} ); '
                         f'CtlDegs.insertLast( {deg!r} );')
    ctl_table = "\n".join(ctl_lines) if ctl_lines else "    // (无舵面偏角)"

    ref_body = ["    array<double> sr_%d; sr_%d.push_back( %s );" % (0, 0, _f(panel["sref"])),
                '    SetDoubleAnalysisInput( "VSPAEROSweep", "Sref", sr_0 );',
                "    array<double> br_%d; br_%d.push_back( %s );" % (0, 0, _f(panel["bref"])),
                '    SetDoubleAnalysisInput( "VSPAEROSweep", "bref", br_0 );',
                "    array<double> cr_%d; cr_%d.push_back( %s );" % (0, 0, _f(panel["cref"])),
                '    SetDoubleAnalysisInput( "VSPAEROSweep", "cref", cr_0 );']
    if panel["ref_mode"] == "model":
        ref_body += [
            "    array<int> rf; rf.push_back( 3 );",
            '    SetIntAnalysisInput( "VSPAEROSweep", "RefFlag", rf );',
            "    array<string> wid; wid.push_back( %s );" % json.dumps(panel.get("ref_wing_id", "")),
            '    SetStringAnalysisInput( "VSPAEROSweep", "WingID", wid );']
        choice = panel.get("ref_choice", "stot")
        if choice in ("mac", "cave"):
            ref_body += ['    array<int> mf; mf.push_back( 1 );',
                         '    SetIntAnalysisInput( "VSPAEROSweep", "MACFlag", mf );']
        if choice == "scurve":
            ref_body += ['    array<int> sf; sf.push_back( 1 );',
                         '    SetIntAnalysisInput( "VSPAEROSweep", "ScurveFlag", sf );']

    cg_body = [
        "    array<int> uc; uc.push_back( 1 );",
        '    SetIntAnalysisInput( "VSPAEROSweep", "UseCGModeFlag", uc );',
        "    array<int> cgs; cgs.push_back( %d );" % wc.MASS_SET,
        '    SetIntAnalysisInput( "VSPAEROSweep", "CGGeomSet", cgs );',
        "    array<double> cgx; cgx.push_back( %s );" % _f(panel["xref"]),
        '    SetDoubleAnalysisInput( "VSPAEROSweep", "Xcg", cgx );',
        "    array<double> cgy; cgy.push_back( %s );" % _f(panel["yref"]),
        '    SetDoubleAnalysisInput( "VSPAEROSweep", "Ycg", cgy );',
        "    array<double> cgz; cgz.push_back( %s );" % _f(panel["zref"]),
        '    SetDoubleAnalysisInput( "VSPAEROSweep", "Zcg", cgz );']

    wk = panel["wake"]
    solver = [
        "    array<int> fi; fi.push_back( %d );" % (1 if wk["fixed"] else 0),
        '    SetIntAnalysisInput( "VSPAEROSweep", "FixedWakeFlag", fi );',
        "    array<int> wi; wi.push_back( %d );" % int(wk["iter"]),
        '    SetIntAnalysisInput( "VSPAEROSweep", "WakeNumIter", wi );',
        "    array<int> wn; wn.push_back( %d );" % int(wk["nodes"]),
        '    SetIntAnalysisInput( "VSPAEROSweep", "NumWakeNodes", wn );',
        "    array<double> wr; wr.push_back( %s );" % _f(wk["relax"]),
        '    SetDoubleAnalysisInput( "VSPAEROSweep", "WakeRelax", wr );',
        "    array<int> fw; fw.push_back( %d );" % int(wk["freeze"]),
        '    SetIntAnalysisInput( "VSPAEROSweep", "FreezeWakeAtIteration", fw );',
        "    array<int> iw; iw.push_back( %d );" % int(wk["implicit_start"]),
        '    SetIntAnalysisInput( "VSPAEROSweep", "ImplicitWakeStartIteration", iw );',
        "    array<int> nc; nc.push_back( %d );" % int(panel["ncpu"]),
        '    SetIntAnalysisInput( "VSPAEROSweep", "NCPU", nc );',
        "    array<int> sy; sy.push_back( %d );" % (1 if panel["symmetry"] else 0),
        '    SetIntAnalysisInput( "VSPAEROSweep", "Symmetry", sy );',
        "    array<int> fe; fe.push_back( %d );" % (1 if panel["fem2d"] else 0),
        '    SetIntAnalysisInput( "VSPAEROSweep", "2DFEMFlag", fe );',
        "    array<int> tp; tp.push_back( %d );" % (1 if panel["tecplot"] else 0),
        '    SetIntAnalysisInput( "VSPAEROSweep", "TecplotFlag", tp );']

    def sweep_vec(key: str, name: str, tag: int) -> list[str]:
        a = panel[key]
        start_nm = "ReCref" if name == "ReCref" else name + "Start"   # ReCref 无 Start 后缀
        return [
            "    array<double> s%d; s%d.push_back( %s );" % (tag, tag, _f(a[0])),
            '    SetDoubleAnalysisInput( "VSPAEROSweep", "%s", s%d );' % (start_nm, tag),
            "    array<double> e%d; e%d.push_back( %s );" % (tag, tag, _f(a[1])),
            '    SetDoubleAnalysisInput( "VSPAEROSweep", "%sEnd", e%d );' % (name, tag),
            "    array<int> n%d; n%d.push_back( %d );" % (tag, tag, int(a[2])),
            '    SetIntAnalysisInput( "VSPAEROSweep", "%sNpts", n%d );' % (name, tag)]

    sweep = []
    for tag, (key, name) in enumerate([("alpha", "Alpha"), ("beta", "Beta"),
                                        ("mach", "Mach"), ("recref", "ReCref")], start=1):
        sweep += sweep_vec(key, name, tag)

    compute = [
        "    array<int> g2; g2.push_back( %d );" % _as_set_int(thick),
        '    SetIntAnalysisInput( "VSPAEROComputeGeometry", "GeomSet", g2 );',
        "    array<int> t2; t2.push_back( %d );" % _as_set_int(thin),
        '    SetIntAnalysisInput( "VSPAEROComputeGeometry", "ThinGeomSet", t2 );',
        "    array<int> sy2; sy2.push_back( %d );" % (1 if panel["symmetry"] else 0),
        '    SetIntAnalysisInput( "VSPAEROComputeGeometry", "Symmetry", sy2 );',
        "    array<int> cf; cf.push_back( %d );" % (1 if panel["cull"]["flag"] else 0),
        '    SetIntAnalysisInput( "VSPAEROComputeGeometry", "CullFracFlag", cf );',
        "    array<double> cv; cv.push_back( %s );" % _f(panel["cull"]["frac"]),
        '    SetDoubleAnalysisInput( "VSPAEROComputeGeometry", "CullFrac", cv );',
        "    array<int> gs; gs.push_back( %d );" % _as_set_int(thick),
        '    SetIntAnalysisInput( "VSPAEROSweep", "GeomSet", gs );',
        "    array<int> ts; ts.push_back( %d );" % _as_set_int(thin),
        '    SetIntAnalysisInput( "VSPAEROSweep", "ThinGeomSet", ts );']

    src = AS_TEMPLATE
    src = src.replace("{{VSP3}}", str(vsp3_path).replace("\\", "/"))
    src = src.replace("{{AERO_SET}}", str(aero_set))
    src = src.replace("{{CTL_TABLE}}", ctl_table)
    src = src.replace("{{REF_BODY}}", "\n".join(ref_body))
    src = src.replace("{{CG_BODY}}", "\n".join(cg_body))
    src = src.replace("{{SOLVER_BODY}}", "\n".join(solver))
    src = src.replace("{{SWEEP_BODY}}", "\n".join(sweep))
    src = src.replace("{{COMPUTE_BODY}}", "\n".join(compute))
    return src


def _as_set_int(v) -> int:
    if v in ("shown", 1, "1"):
        return 1
    if v in ("none", -1, "-1"):
        return -1
    return int(v)


# ============================ 结果汇总 ============================

def summarize(polar_rows: list[dict], panel: dict) -> dict:
    """从去重工况提取图表序列与稳定性摘要 (dCm/dCL 回归 → Xnp/SM)。"""
    rows = sorted(polar_rows, key=lambda r: r["aoa_deg"])
    alpha = [r["aoa_deg"] for r in rows]
    cl = [r["CL"] for r in rows]
    cd = [r["CD"] for r in rows]
    cm = [r["Cm"] for r in rows]
    ld = [r["CL"] / r["CD"] if r["CD"] > 1e-12 else float("nan") for r in rows]

    dCmdCL = None
    if len(rows) >= 2:
        n = len(rows)
        sx = sum(cl)
        sxx = sum(v * v for v in cl)
        sy = sum(cm)
        sxy = sum(a * b for a, b in zip(cl, cm))
        denom = n * sxx - sx * sx
        if abs(denom) > 1e-12:
            dCmdCL = (n * sxy - sx * sy) / denom
    cref = panel["cref"]
    xref = panel["xref"]
    xnp = xref - cref * dCmdCL if dCmdCL is not None else None
    return {
        "alpha": alpha, "CL": cl, "CD": cd, "Cm": cm, "LD": ld,
        "beta": [r["beta_deg"] for r in rows], "mach": [r["mach"] for r in rows],
        "dCm_dCL": dCmdCL, "Xnp_m": xnp,
        "cref": cref, "xref_m": xref,
    }


def main(argv=None) -> int:
    ap = argparse.ArgumentParser(description="工坊栏② VSPAERO 单次计算")
    ap.add_argument("--vsp", required=True)
    ap.add_argument("--panel", required=True)
    ap.add_argument("--work", default=None)
    ap.add_argument("--openvsp-dir", default=None)
    args = ap.parse_args(argv)

    t0 = time.time()
    src_vsp = Path(args.vsp)
    if not src_vsp.is_file():
        print(f"[fail] vsp3 不存在: {src_vsp}")
        return 1
    # F1 前置校验: 部件名含 '_' 或非 ASCII 时 VSPAERO 操纵面 tag 匹配断裂
    # (实测 "C_Wing" → "Cing", 不产出 polar) — 提前给明确报错, 不跑空计算
    bad = wc.bad_geom_names(wc.scan_vsp3_geom_names(src_vsp))
    if bad:
        print("[fail] 部件名含 '_' 或非 ASCII, VSPAERO 操纵面匹配会断裂: "
              + ", ".join(bad) + " — 请在工坊/OpenVSP 中改名为纯字母数字后重试")
        return 1
    panel = dict(_DEFAULTS)
    panel.update(json.loads(Path(args.panel).read_text(encoding="utf-8")))
    work = Path(args.work) if args.work else ROOT / "work" / "workshop_aero"
    work.mkdir(parents=True, exist_ok=True)
    openvsp = Path(args.openvsp_dir) if args.openvsp_dir else wc.vsproot()
    vsp_script = openvsp / "vspscript.exe"
    if not vsp_script.is_file():
        print(f"[fail] 找不到 vspscript.exe: {vsp_script}")
        return 1

    print(f"[stage] 1/4 准备规整副本 (输出目录 {work})")
    work_abs = work.resolve()
    local = work_abs / (src_vsp.stem + "_aero.vsp3")
    shutil.copyfile(src_vsp, local)

    print("[stage] 2/4 生成 AngelScript")
    case = work / "workshop_aero_case.as"
    src = render_case(local, panel)
    if "{{" in src:
        print("[fail] 模板存在未烘焙占位符")
        return 1
    case.write_text(src, encoding="utf-8", newline="\n")

    print("[stage] 3/4 VSPAERO 计算 (独立 vspscript 进程)")
    # 运行前只清旧"输出" (铁律 1: VSPAEROSweep 会清上一轮结果); 不得动输入 .vsp3
    out_exts = (".polar", ".history", ".lod", ".stab", ".vspgeom", ".forces",
                ".pstab", ".qstab", ".rstab", ".def", ".adm")
    for old in work_abs.iterdir():
        if old.name.startswith(local.stem) and old.suffix.lower() in out_exts:
            old.unlink()
    try:
        proc = subprocess.run(
            [str(vsp_script), "-script", str(case.resolve())],
            cwd=str(work_abs), capture_output=True, text=True, timeout=1800,
            encoding="utf-8", errors="replace")
    except subprocess.TimeoutExpired:
        print("[fail] vspscript 超时 (30 分钟)")
        return 1
    for line in (proc.stdout or "").splitlines():
        if line.strip():
            print("[line] " + line.strip())
    # 注意: 安装版 vspscript 正常结束也返回 1 (自编译 Release 才是 0) —
    # 成败以逻辑标志为准: sweep 完成标记 + 无 AERO_FAIL + polar 产出
    ok = ("[AERO_STAGE] sweep done" in (proc.stdout or "")
          and "[AERO_FAIL]" not in (proc.stdout or ""))
    if not ok:
        print(f"[fail] VSPAERO 未完成 (vspscript 退出码 {proc.returncode}); stderr 见下")
        for line in (proc.stderr or "").splitlines()[-10:]:
            print("[line] " + line.strip())
        return 1

    print("[stage] 4/4 收集结果")
    polar = work_abs / (local.stem + ".polar")
    if not polar.is_file():
        print(f"[fail] 未产出 .polar: {polar}")
        return 1
    try:
        rows = parse_polar_full(polar)
    except ValueError as e:
        print(f"[fail] .polar 解析失败: {e}")
        return 1
    if not rows:
        print("[fail] .polar 无有效工况")
        return 1
    result = summarize(rows, panel)
    result["source_polar"] = str(polar)
    result["panel"] = panel
    out_json = work / "aero_result.json"
    out_json.write_text(json.dumps(result, ensure_ascii=False, indent=1),
                        encoding="utf-8")
    print(f"[done] {out_json} (用时 {time.time() - t0:.1f}s, 工况 {len(rows)})")
    return 0


if __name__ == "__main__":
    sys.exit(main())
