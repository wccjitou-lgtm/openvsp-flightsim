#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""
import_vsp.py — Phase 7a: 任意 .vsp3 机型一键导入管线 (编排器, 集成开发提示词.md)

流程 (每阶段独立 vspscript 进程; VSPAEROSweep 会清上一轮输出, 结束立即改名保全):
  prep        规整: ReadVSPFile → 用户集3气动/用户集4质量 → 网格密度统一
              → 舵面保障(require/auto+split-wing反对称) → 参考翼 → MassProp/Xcg
              → 存规整版 + STL
  3×case      STAB_DEFAULT / BETA_SWEEP / ALPHA_SWEEP (薄/厚面集合可选, 力矩参考
              位置 CG 模式集合可选; 舵面提取失败的病态面自动迁移重试)
  gen         generate_aircraft_config.py + stl_to_obj_unity.py
  validate    关键导数非零校验

薄/厚面集合 (VSPAERO GeomSet=厚面 / ThinGeomSet=薄面, OpenVSP SET_FIRST_USER=3):
  thin (默认): 未忽略几何入 Set3, GeomSet=-1, ThinGeomSet=3
  thick:       同一气动子集 GeomSet=3, ThinGeomSet=-1
力矩参考位置: UseCGModeFlag=1 + CGGeomSet=完整质量集(4)/全部(0), 忽略部件仍保留质量。

产物: VspImports/<内部ASCII名>/{AircraftConfig.json, model.obj, <名>_normalized.vsp3,
      report.json}; 目录名用 ASCII 内部名 (vspscript/vspaero 窄字符写盘 + 中文路径会
      乱码/失败), 显示名写在 config.name 与 report.displayName。

进度行: [stage] i/N label / [ok] label / [fail] ... / [done] 产物: 目录 (用时 Ns)

用法:
  python Tools/import_vsp.py --vsp <file.vsp3> [--name X] [--alpha 2] [--mach 0.06]
      [--wake 5] [--beta-range "-6,6,5"] [--alpha-range "-4,10,7"]
      [--max-thrust N] [--auto-controls] [--tess 16] [--tess 0=不改]
      [--surface-set thin|thick] [--ref-set thin|thick]
      [--out-dir VspImports/X] [--openvsp-dir <含vspscript.exe的目录>]
"""

from __future__ import annotations

import argparse
import glob
import hashlib
import json
import re
import shutil
import subprocess
import sys
import time
from pathlib import Path

ROOT = Path(__file__).resolve().parent.parent
TOOLS = ROOT / "Tools"
# 可用捆绑的 <根>/OpenVSP；其他环境通过 --openvsp-dir 或 VSPROOT 配置。
BUNDLED = ROOT / "OpenVSP"
DEFAULT_OPENVSP = BUNDLED if (BUNDLED / "vspscript.exe").is_file() else None

SET_THIN_G = -1   # GeomSet (厚面集合): -1 = 无
SET_THIN_T = 3    # ThinGeomSet (薄面集合): 用户集3 (SET_FIRST_USER)
SET_THICK_G = 3   # Both solvers use the persisted aerodynamic subset.
SET_THICK_T = -1

# ============================ AngelScript 模板 ============================
# 参数 {{TOKEN}} 烘焙; Print 一律 ASCII。路径一律绝对+ASCII (SetVSP3FileName 决定
# 全部输出文件名, 自编译版不会从 ReadVSPFile 自动继承, 必须显式设置)。

MASS_FUNCTION = r'''
string RunIdentifiedMassProps()
{
    array<string> allids = FindGeoms();
    array<string> names;
    for ( uint i = 0; i < allids.size(); i++ )
    {
        string nm = GetGeomName( allids[i] );
        names.push_back( nm );
        Print( "[COMPONENT] " + allids[i] + "|" + nm + "\n" );
        SetGeomName( allids[i], "mass_" + allids[i] );
        SetSetFlag( allids[i], 1, true );
    }
    Update();
    SetAnalysisInputDefaults( "MassProp" );
    string mp = ExecAnalysis( "MassProp" );
    for ( uint i = 0; i < allids.size(); i++ )
        SetGeomName( allids[i], names[i] );
    Update();
    return mp;
}
'''

PREP_TEMPLATE = r'''
const string SRC  = "{{SRC}}";
const string NAME = "{{NAME}}";
const string WORK = "{{WORK}}";        // work 目录绝对路径 (ASCII)
const bool   AUTO_CONTROLS = {{AUTO}};
const int    TESS = {{TESS}};

// R4 分部件导出辅助: 删除 AddMeshGeom 遗留的 MESH 型几何
// (ExportFile(STL) 每次会按集合现烘一个 MESH, 不删则后续导出复用缓存无法分体)
void DeleteMeshGeoms()
{
    array<string> ids = FindGeoms();
    array<string> del;
    for ( uint i = 0; i < ids.size(); i++ )
    {
        if ( GetGeomTypeName( ids[i] ) == "Mesh" ) del.insertLast( ids[i] );
    }
    if ( del.size() > 0 ) DeleteGeomVec( del );
}

{{MASS_FUNCTION}}

bool OvMatches( string gid, string selector )
{
    if ( selector.find( "geom:" ) == 0 ) return gid == selector.substr( 5 );
    return GetGeomName( gid ) == selector;
}

// ==== 部件覆盖 (工坊编辑烘焙; 空 = 无覆盖) ====
string OvRoleOf( string gid )
{
{{OV_ROLE_BODY}}
    return "";
}

string OvCtlOf( string gid )
{
{{OV_CTL_BODY}}
    return "";
}

array<string> g_mirrorFix;

bool IsWing( string id ) { return GetGeomTypeName( id ) == "Wing"; }

double WingSpan( string id )
{
    if ( !IsWing( id ) ) return -1.0;
    string p = FindParm( id, "Span", "XSec_1" );   // FindParm(几何ID, 参数名, 组名)
    if ( p.size() == 0 ) return 0.0;
    return GetParmVal( p );
}

bool IsVertical( string id )
{
    string p = FindParm( id, "X_Rel_Rotation", "XForm" );
    if ( p.size() == 0 ) return false;
    double r = GetParmVal( p );
    return ( abs( r - 90.0 ) < 10.0 ) || ( abs( r - 270.0 ) < 10.0 ) || ( abs( r + 90.0 ) < 10.0 );
}

double MaxHorizontalSpan( array<string> ids )
{
    double bs = -1.0;
    for ( uint i = 0; i < ids.size(); i++ )
    {
        double s = WingSpan( ids[i] );
        if ( s > 0 && !IsVertical( ids[i] ) && s > bs ) bs = s;
    }
    return bs;
}

int CountControlSubSurfs( array<string> ids )
{
    int n = 0;
    for ( uint i = 0; i < ids.size(); i++ )
    {
        array<string> ss = GetSubSurfIDVec( ids[i] );
        for ( uint k = 0; k < ss.size(); k++ )
            if ( GetSubSurfType( ss[k] ) == SS_CONTROL ) n++;
    }
    return n;
}

int CountNamed( array<string> ids, string nm )
{
    int n = 0;
    for ( uint i = 0; i < ids.size(); i++ )
    {
        array<string> ss = GetSubSurfIDVec( ids[i] );
        for ( uint k = 0; k < ss.size(); k++ )
            if ( GetSubSurfName( ids[i], ss[k] ) == nm ) n++;
    }
    return n;
}

void AddCtl( string geom, string cname, double e0, double e1, double lc, bool mirrorFix )
{
    string ss = AddSubSurf( geom, SS_CONTROL );
    SetSubSurfName( geom, ss, cname );
    SetParmValUpdate( ss, "EtaFlag", "SS_Control", 1 );
    SetParmValUpdate( ss, "EtaStart", "SS_Control", e0 );
    SetParmValUpdate( ss, "EtaEnd", "SS_Control", e1 );
    SetParmValUpdate( ss, "Length_C_Start", "SS_Control", lc );
    SetParmValUpdate( ss, "Length_C_End", "SS_Control", lc );
    if ( mirrorFix ) g_mirrorFix.push_back( ss );
}

void AutoAddControls( array<string> ids )
{
    double maxSpan = MaxHorizontalSpan( ids );
    if ( maxSpan <= 0 ) return;
    int nElev = 0, nRud = 0;
    for ( uint i = 0; i < ids.size(); i++ )
    {
        if ( !IsWing( ids[i] ) ) continue;
        double s = WingSpan( ids[i] );
        if ( s <= 0 ) continue;
        string gname = GetGeomName( ids[i] );
        string role = OvRoleOf( ids[i] );      // 工坊指定类别优先
        if ( role == "" )
        {
            if ( IsVertical( ids[i] ) ) role = "vtail";
            else if ( s >= 0.9 * maxSpan ) role = "wing";
            else role = "htail";
        }
        if ( role == "vtail" ) { AddCtl( ids[i], "Rudder", 0.10, 0.95, 0.35, true ); nRud++; }
        else if ( role == "wing" ) AddCtl( ids[i], "Aileron", 0.60, 0.95, 0.25, false );
        else if ( role == "htail" ) { AddCtl( ids[i], "Elevator", 0.10, 0.95, 0.35, true ); nElev++; }
    }
    Print( "[PREP_WARN] auto controls: split/main-wing Aileron, Elevator x" + nElev + ", Rudder x" + nRud + " (heuristic, verify in OpenVSP)\n" );
}

void FixGains()
{
    string vsp_set = FindContainer( "VSPAEROSettings", 0 );
    int ngrp = GetNumControlSurfaceGroups();
    // 已命名的 Elevator/Rudder (工坊/手工建) 不会进 g_mirrorFix, 按名补入
    array<string> mirrorIds = g_mirrorFix;
    array<string> ails;
    array<string> ids = FindGeoms();
    for ( uint i = 0; i < ids.size(); i++ )
    {
        if ( !GetSetFlag( ids[i], 3 ) ) continue;
        array<string> ss = GetSubSurfIDVec( ids[i] );
        for ( uint k = 0; k < ss.size(); k++ )
        {
            if ( GetSubSurfType( ss[k] ) != SS_CONTROL ) continue;
            string nm = GetSubSurfName( ids[i], ss[k] );
            if ( nm == "Elevator" || nm == "Rudder" ) mirrorIds.push_back( ss[k] );
        }
    }
    for ( uint i = 0; i < mirrorIds.size(); i++ )
        for ( int gi = 0; gi <= ngrp; gi++ )
        {
            string g1 = FindParm( vsp_set, "Surf_" + mirrorIds[i] + "_1_Gain", "ControlSurfaceGroup_" + gi );
            if ( g1.size() > 0 ) { SetParmValUpdate( g1, -1.0 ); break; }
        }
    for ( uint i = 0; i < ids.size(); i++ )
    {
        if ( !GetSetFlag( ids[i], 3 ) ) continue;
        array<string> ss = GetSubSurfIDVec( ids[i] );
        for ( uint k = 0; k < ss.size(); k++ )
            if ( GetSubSurfType( ss[k] ) == SS_CONTROL &&
                 GetSubSurfName( ids[i], ss[k] ) == "Aileron" ) ails.push_back( ss[k] );
    }
    for ( uint a = 1; a < ails.size(); a++ )
        for ( int gi = 0; gi <= ngrp; gi++ )
        {
            string g0 = FindParm( vsp_set, "Surf_" + ails[a] + "_0_Gain", "ControlSurfaceGroup_" + gi );
            if ( g0.size() > 0 ) { SetParmValUpdate( g0, -1.0 ); break; }
        }
    if ( ails.size() > 1 ) Print( "split-wing aileron antisymmetry applied\n" );
}

void main()
{
    Print( "=== import prep: " + NAME + " ===\n" );
    ReadVSPFile( SRC );
    array<string> ids = FindGeoms();
    if ( ids.size() == 0 ) { Print( "[PREP_FAIL] no geometry loaded: " + SRC + "\n" ); return; }
    Print( "loaded " + ids.size() + " geoms\n" );

    // U8: 源 vsp3 可能带 worker MassProp 残留的 MESH 型几何 (旧工程文件) —
    // 读入即清: ExportFile(STL) 见集合内已有 MESH 会直接导出这份陈旧部分网格
    // 而不重新烘整机 (实测整机 STL 缺前机身, auto_gear 据此把前轮放到重心后面)
    DeleteMeshGeoms();
    ids = FindGeoms();

    // Set3 is the aerodynamic subset; Set4 preserves the complete mass model.
    for ( uint i = 0; i < ids.size(); i++ )
    {
        SetSetFlag( ids[i], 1, true );
        SetSetFlag( ids[i], 3, true );
        SetSetFlag( ids[i], 4, true );
    }

    // 网格密度统一 (过密网格 → VSPAERO GMRES 首迭代 NaN, 弹簧刀机型实测)
    if ( TESS > 0 )
    {
        int nTess = 0;
        for ( uint i = 0; i < ids.size(); i++ )
        {
            string tu = FindParm( ids[i], "Tess_U", "Shape" );
            string tw = FindParm( ids[i], "Tess_W", "Shape" );
            if ( tu.size() > 0 ) { SetParmValUpdate( tu, TESS ); nTess++; }
            if ( tw.size() > 0 ) { SetParmValUpdate( tw, TESS ); nTess++; }
        }
        Print( "tess set to " + TESS + " on " + nTess + " parms\n" );
    }

    // ==== 部件覆盖应用 (工坊编辑): 减重/安装角/忽略/舵面指定 ====
{{OV_APPLY_BODY}}

    array<string> active;
    for ( uint i = 0; i < ids.size(); i++ )
        if ( GetSetFlag( ids[i], 3 ) ) active.push_back( ids[i] );
    ids = active;
    if ( ids.size() == 0 ) { Print( "[PREP_FAIL] all geometry ignored\n" ); return; }

    // 舵面保障 (真实 SS_Control 子面数为准; vsp3 空组容器会骗过 GetNumControlSurfaceGroups)
    AutoGroupVSPAEROControlSurfaces();
    int nctl = CountControlSubSurfs( ids );
    Print( "control subsurfaces: " + nctl + "\n" );
    if ( nctl == 0 )
    {
        if ( !AUTO_CONTROLS )
        {
            Print( "[PREP_FAIL] no SS_Control subsurfaces. Add Aileron/Elevator/Rudder in OpenVSP, or re-run with --auto-controls.\n" );
            return;
        }
        AutoAddControls( ids );
        nctl = CountControlSubSurfs( ids );
        if ( nctl == 0 ) { Print( "[PREP_FAIL] auto controls failed (no WING found)\n" ); return; }
        AutoGroupVSPAEROControlSurfaces();
        FixGains();
    }

    // 规范化默认名舵面 (SS_CONT_N): 按所在几何类别命名, 生成器按名映射三轴。
    // 用户自定义名 (Flap 等) 不动。实测 logisitc 机型升降舵叫 SS_CONT_0 导致缺轴。
    {
        int nRenamed = 0;
        double maxSpan2 = MaxHorizontalSpan( ids );
        for ( uint i = 0; i < ids.size(); i++ )
        {
            array<string> ss = GetSubSurfIDVec( ids[i] );
            for ( uint k = 0; k < ss.size(); k++ )
            {
                if ( GetSubSurfType( ss[k] ) != SS_CONTROL ) continue;
                string nm = GetSubSurfName( ids[i], ss[k] );
                if ( nm.find( "SS_CONT_" ) != 0 ) continue;
                string proper = "Aileron";
                if ( IsVertical( ids[i] ) ) proper = "Rudder";
                else if ( IsWing( ids[i] ) && maxSpan2 > 0 &&
                          WingSpan( ids[i] ) < 0.9 * maxSpan2 ) proper = "Elevator";
                SetSubSurfName( ids[i], ss[k], proper );
                nRenamed++;
            }
        }
        if ( nRenamed > 0 ) Print( "renamed " + nRenamed + " default-named controls by geometry class\n" );
        if ( nRenamed > 0 ) AutoGroupVSPAEROControlSurfaces();
    }
    // 审查 R09: 增益修正无条件执行 — 手工/工坊命名的对称升降舵 (+1/+1 俯仰效能≈0)
    // 与分片副翼同样需要规范化; 已修正过的幂等 (设 -1 不变), 既有机型输出不受影响。
    FixGains();

    // 参考翼 = 工坊指定的 wing 类部件优先, 否则最大展长水平翼
    string best = ""; double bs = -1.0;
    for ( uint i = 0; i < ids.size(); i++ )
    {
        if ( OvRoleOf( ids[i] ) == "wing" ) { best = ids[i]; break; }
    }
    if ( best.size() == 0 )
    {
        for ( uint i = 0; i < ids.size(); i++ )
        {
            double s = WingSpan( ids[i] );
            if ( s > 0 && !IsVertical( ids[i] ) && s > bs ) { bs = s; best = ids[i]; }
        }
    }
    if ( best.size() > 0 ) { SetVSPAERORefWingID( best ); Print( "ref wing: " + GetGeomName( best ) + "\n" ); }
    else Print( "[PREP_WARN] no horizontal wing for ref, Sref/bref/cref keep file values\n" );
    Update();

    // 先另存到 work (ASCII 路径) — 决定全部输出文件名 + MassProps 报告落点
    SetVSP3FileName( WORK + "/" + NAME + "_normalized.vsp3" );
    WriteVSPFile( NAME + "_normalized.vsp3", SET_ALL );

    // MassProp → Xcg (删临时网格 + 恢复显示)
    string mp_res = RunIdentifiedMassProps();
    array<string> mp_mesh_ids = GetStringResults( mp_res, "Mesh_GeomID" );
    DeleteGeomVec( mp_mesh_ids );
    array<vec3d> cg_arr = GetVec3dResults( mp_res, "Total_CG" );
    array<double> mass_arr = GetDoubleResults( mp_res, "Total_Mass" );
    Print( "[MASS] " + mass_arr[0] + "\n" );
    Print( "[CG] " + cg_arr[0].x() + " " + cg_arr[0].y() + " " + cg_arr[0].z() + "\n" );

    string vsp_set = FindContainer( "VSPAEROSettings", 0 );
    SetParmValUpdate( vsp_set, "Xcg", "VSPAERO", cg_arr[0].x() );
    SetParmValUpdate( vsp_set, "Ycg", "VSPAERO", cg_arr[0].y() );
    SetParmValUpdate( vsp_set, "Zcg", "VSPAERO", cg_arr[0].z() );

    array<string> ids2 = FindGeoms();
    for ( uint i = 0; i < ids2.size(); i++ )
        SetSetFlag( ids2[i], 1, GetSetFlag( ids2[i], 3 ) );
    Update();

    WriteVSPFile( NAME + "_normalized.vsp3", SET_ALL );
    ExportFile( NAME + ".stl", 3, EXPORT_STL );

    // ==== R4: 分部件 STL (Unity 分组网格用) ====
    // ExportFile(STL) 只写 MESH 型几何: 集合里没有时 AddMeshGeom(集合) 把整机烘成
    // 单个 MESH 并留在场景, 之后 ExistMesh 恒真 → 必须每轮删掉遗留 MESH 才能按
    // 集合成员分体 (三角形守恒已验证: 各部件和 == 整机)。文件名用 part_<i>.stl
    // (几何名可能含非 ASCII, 索引名避免窄字符写盘失败), [PART] <i> <名> 供 Python
    // 侧建 manifest。
    array<string> part_ids;
    for ( uint i = 0; i < ids2.size(); i++ )
        if ( GetSetFlag( ids2[i], 3 ) ) part_ids.insertLast( ids2[i] );
    DeleteMeshGeoms();
    for ( uint i = 0; i < part_ids.size(); i++ )
    {
        for ( uint j = 0; j < part_ids.size(); j++ )
            SetSetFlag( part_ids[j], 3, j == i );
        Update();
        ExportFile( WORK + "/parts/part_" + i + ".stl", 3, EXPORT_STL );
        Print( "[PART] " + i + " " + GetGeomName( part_ids[i] ) + "\n" );
        DeleteMeshGeoms();
    }
    for ( uint j = 0; j < part_ids.size(); j++ )
        SetSetFlag( part_ids[j], 3, true );
    Update();
    Print( "[PARTS_DONE] " + part_ids.size() + "\n" );

    Print( "[SREF] " + GetParmVal( FindParm( vsp_set, "Sref", "VSPAERO" ) ) + "\n" );
    Print( "[BREF] " + GetParmVal( FindParm( vsp_set, "bref", "VSPAERO" ) ) + "\n" );
    Print( "[CREF] " + GetParmVal( FindParm( vsp_set, "cref", "VSPAERO" ) ) + "\n" );
    Print( "[PREP_OK]\n" );
}
'''

CASE_TEMPLATE = r'''
const string NAME = "{{NAME}}";
const string WORK = "{{WORK}}";
const double CRUISE_ALPHA = {{ALPHA}};
const double CRUISE_MACH  = {{MACH}};
const int    WAKE_ITER    = {{WAKE}};
const double BETA_START   = {{BETA_START}};
const double BETA_END     = {{BETA_END}};
const int    BETA_NPTS    = {{BETA_NPTS}};
const double ALPHA_START  = {{ALPHA_START}};
const double ALPHA_END    = {{ALPHA_END}};
const int    ALPHA_NPTS   = {{ALPHA_NPTS}};
const int    GSET = {{GSET}};      // 厚面集合 (thin: -1)
const int    TSET = {{TSET}};      // 薄面集合 (thin: 3=用户集3)
const int    CGSET = {{CGSET}};    // 力矩参考集合
const bool   USE_CG  = {{USE_CG}}; // 力矩参考用集合 CG 模式

const int STAB_OFF     = 0;
const int STAB_DEFAULT = 1;

void main()
{
    string mode = "UNKNOWN";
    {
        file@ f = file();
        if ( f.open( "case.txt", "r" ) >= 0 )
        {
            if ( !f.isEndOfFile() ) mode = f.readLine();
            f.close();
        }
    }
    Print( "=== import case: " + mode + " (" + NAME + ") ===\n" );

    ReadVSPFile( WORK + "/" + NAME + "_normalized.vsp3" );
    SetVSP3FileName( WORK + "/" + NAME + "_normalized.vsp3" );
    array<string> ids = FindGeoms();
    if ( ids.size() == 0 ) { Print( "[CASE_FAIL] model load empty\n" ); return; }
    for ( uint i = 0; i < ids.size(); i++ )
    {
        SetSetFlag( ids[i], 1, GetSetFlag( ids[i], 3 ) );
    }
    AutoGroupVSPAEROControlSurfaces();
    Update();

    SetAnalysisInputDefaults( "VSPAEROSweep" );
    array<int> gs; gs.push_back( GSET );
    SetIntAnalysisInput( "VSPAEROSweep", "GeomSet", gs );
    array<int> ts; ts.push_back( TSET );
    SetIntAnalysisInput( "VSPAEROSweep", "ThinGeomSet", ts );
    array<int> rf; rf.push_back( 3 );
    SetIntAnalysisInput( "VSPAEROSweep", "RefFlag", rf );
    if ( USE_CG )
    {
        array<int> uc; uc.push_back( 1 );
        SetIntAnalysisInput( "VSPAEROSweep", "UseCGModeFlag", uc );
        array<int> cg; cg.push_back( CGSET );
        SetIntAnalysisInput( "VSPAEROSweep", "CGGeomSet", cg );
    }
    array<double> mach; mach.push_back( CRUISE_MACH );
    SetDoubleAnalysisInput( "VSPAEROSweep", "MachStart", mach );
    array<int> mn; mn.push_back( 1 );
    SetIntAnalysisInput( "VSPAEROSweep", "MachNpts", mn );
    array<int> wk; wk.push_back( WAKE_ITER );
    SetIntAnalysisInput( "VSPAEROSweep", "WakeNumIter", wk );

    if ( mode == "STAB_DEFAULT" )
    {
        array<double> a; a.push_back( CRUISE_ALPHA );
        SetDoubleAnalysisInput( "VSPAEROSweep", "AlphaStart", a );
        array<int> an; an.push_back( 1 );
        SetIntAnalysisInput( "VSPAEROSweep", "AlphaNpts", an );
        array<double> b; b.push_back( 0.0 );
        SetDoubleAnalysisInput( "VSPAEROSweep", "BetaStart", b );
        array<int> bn; bn.push_back( 1 );
        SetIntAnalysisInput( "VSPAEROSweep", "BetaNpts", bn );
        array<int> st; st.push_back( STAB_DEFAULT );
        SetIntAnalysisInput( "VSPAEROSweep", "UnsteadyType", st );
    }
    else if ( mode == "BETA_SWEEP" )
    {
        array<double> a; a.push_back( CRUISE_ALPHA );
        SetDoubleAnalysisInput( "VSPAEROSweep", "AlphaStart", a );
        array<int> an; an.push_back( 1 );
        SetIntAnalysisInput( "VSPAEROSweep", "AlphaNpts", an );
        array<double> b; b.push_back( BETA_START );
        SetDoubleAnalysisInput( "VSPAEROSweep", "BetaStart", b );
        array<double> b2; b2.push_back( BETA_END );
        SetDoubleAnalysisInput( "VSPAEROSweep", "BetaEnd", b2 );
        array<int> bn; bn.push_back( BETA_NPTS );
        SetIntAnalysisInput( "VSPAEROSweep", "BetaNpts", bn );
        array<int> st; st.push_back( STAB_OFF );
        SetIntAnalysisInput( "VSPAEROSweep", "UnsteadyType", st );
    }
    else
    {
        array<double> a; a.push_back( ALPHA_START );
        SetDoubleAnalysisInput( "VSPAEROSweep", "AlphaStart", a );
        array<double> a2; a2.push_back( ALPHA_END );
        SetDoubleAnalysisInput( "VSPAEROSweep", "AlphaEnd", a2 );
        array<int> an; an.push_back( ALPHA_NPTS );
        SetIntAnalysisInput( "VSPAEROSweep", "AlphaNpts", an );
        array<double> b; b.push_back( 0.0 );
        SetDoubleAnalysisInput( "VSPAEROSweep", "BetaStart", b );
        array<int> bn; bn.push_back( 1 );
        SetIntAnalysisInput( "VSPAEROSweep", "BetaNpts", bn );
        array<int> st; st.push_back( STAB_OFF );
        SetIntAnalysisInput( "VSPAEROSweep", "UnsteadyType", st );
    }
    Update();

    SetAnalysisInputDefaults( "VSPAEROComputeGeometry" );
    array<int> g2; g2.push_back( GSET );
    SetIntAnalysisInput( "VSPAEROComputeGeometry", "GeomSet", g2 );
    array<int> t2; t2.push_back( TSET );
    SetIntAnalysisInput( "VSPAEROComputeGeometry", "ThinGeomSet", t2 );
    ExecAnalysis( "VSPAEROComputeGeometry" );
    ExecAnalysis( "VSPAEROSweep" );
    Print( "=== case DONE (" + mode + ") ===\n" );
}
'''

# 病态舵面迁移兜底: 删除 <geom_base> 系几何上名为 <ctl> 的子面;
# 若全机再无该舵 → 迁移到主翼内侧 (升降副翼布局), 保证该轴仍有操纵.
FIXUP_TEMPLATE = r'''
const string NAME = "{{NAME}}";
const string WORK = "{{WORK}}";
const string GBASE = "{{GBASE}}";   // 失败面的几何名基 (如 Backwing)
const string CTL  = "{{CTL}}";      // 失败舵面名 (如 Elevator)

void main()
{
    ReadVSPFile( WORK + "/" + NAME + "_normalized.vsp3" );
    SetVSP3FileName( WORK + "/" + NAME + "_normalized.vsp3" );
    array<string> ids = FindGeoms();
    if ( ids.size() == 0 ) { Print( "[FIX_FAIL] model load empty\n" ); return; }
    array<string> active;
    for ( uint i = 0; i < ids.size(); i++ )
        if ( GetSetFlag( ids[i], 3 ) ) active.push_back( ids[i] );
    ids = active;

    int removed = 0;
    for ( uint i = 0; i < ids.size(); i++ )
    {
        string gname = GetGeomName( ids[i] );
        if ( gname.find( GBASE ) < 0 ) continue;
        array<string> ss = GetSubSurfIDVec( ids[i] );
        for ( int k = ss.size() - 1; k >= 0; k-- )
            if ( GetSubSurfName( ids[i], ss[k] ) == CTL )
            {
                DeleteSubSurf( ids[i], ss[k] );
                removed++;
            }
    }
    Print( "removed " + removed + " " + CTL + " on *" + GBASE + "*\n" );

    int left = 0;
    for ( uint i = 0; i < ids.size(); i++ )
    {
        array<string> ss = GetSubSurfIDVec( ids[i] );
        for ( uint k = 0; k < ss.size(); k++ )
            if ( GetSubSurfName( ids[i], ss[k] ) == CTL ) left++;
    }

    if ( left == 0 )
    {
        // 按轴重定位: Elevator→主翼内侧 (升降副翼); Aileron→主翼外侧;
        // Rudder→最大垂尾中段 (换范围重试提取)
        double maxSpan = -1.0;
        for ( uint i = 0; i < ids.size(); i++ )
        {
            string p = FindParm( ids[i], "Span", "XSec_1" );
            string r = FindParm( ids[i], "X_Rel_Rotation", "XForm" );
            if ( p.size() == 0 ) continue;
            double rot = 0.0; if ( r.size() > 0 ) rot = GetParmVal( r );
            if ( abs( rot - 90.0 ) < 10.0 || abs( rot - 270.0 ) < 10.0 ) continue;
            double s = GetParmVal( p );
            if ( s > maxSpan ) maxSpan = s;
        }
        int added = 0;
        for ( uint i = 0; i < ids.size(); i++ )
        {
            string p = FindParm( ids[i], "Span", "XSec_1" );
            if ( p.size() == 0 ) continue;
            double s = GetParmVal( p );
            string r = FindParm( ids[i], "X_Rel_Rotation", "XForm" );
            double rot = 0.0; if ( r.size() > 0 ) rot = GetParmVal( r );
            bool vert = abs( rot - 90.0 ) < 10.0 || abs( rot - 270.0 ) < 10.0;

            double e0 = 0, e1 = 0, lc = 0; bool add = false;
            if ( CTL == "Rudder" && vert ) { e0 = {{RUD_E0}}; e1 = {{RUD_E1}}; lc = 0.30; add = true; }
            else if ( CTL != "Rudder" && !vert && s >= 0.9 * maxSpan )
            {
                if ( CTL == "Elevator" ) { e0 = 0.05; e1 = 0.50; lc = 0.30; add = true; }
                else { e0 = 0.60; e1 = 0.95; lc = 0.25; add = true; }
            }
            if ( !add ) continue;
            string ss = AddSubSurf( ids[i], SS_CONTROL );
            SetSubSurfName( ids[i], ss, CTL );
            SetParmValUpdate( ss, "EtaFlag", "SS_Control", 1 );
            SetParmValUpdate( ss, "EtaStart", "SS_Control", e0 );
            SetParmValUpdate( ss, "EtaEnd", "SS_Control", e1 );
            SetParmValUpdate( ss, "Length_C_Start", "SS_Control", lc );
            SetParmValUpdate( ss, "Length_C_End", "SS_Control", lc );
            added++;
        }
        Print( "[PREP_WARN] relocated " + CTL + " x" + added + "\n" );
    }
    AutoGroupVSPAEROControlSurfaces();
    Update();
    WriteVSPFile( NAME + "_normalized.vsp3", SET_ALL );
    Print( "[FIX_OK]\n" );
}
'''


# 部件清单 (快, 含 MassProp 部件质量): 供工坊 UI 展示/编辑部件
INSPECT_TEMPLATE = r'''
const string SRC  = "{{SRC}}";
const string NAME = "{{NAME}}";
const string WORK = "{{WORK}}";

{{MASS_FUNCTION}}

void main()
{
    Print( "=== inspect ===\n" );
    ReadVSPFile( SRC );
    array<string> ids = FindGeoms();
    if ( ids.size() == 0 ) { Print( "[INSPECT_FAIL] no geometry loaded\n" ); return; }
    // 报告 (含 MassProps) 写在当前模型文件所在目录 — 先指到 work, 不污染用户源目录
    SetVSP3FileName( WORK + "/" + NAME + "_normalized.vsp3" );
    for ( uint i = 0; i < ids.size(); i++ ) { SetSetFlag( ids[i], 1, true ); }
    Update();
    string mp = RunIdentifiedMassProps();
    array<string> mm = GetStringResults( mp, "Mesh_GeomID" );
    DeleteGeomVec( mm );
    Update();
    for ( uint i = 0; i < ids.size(); i++ )
    {
        string id = ids[i];
        string gname = GetGeomName( id );
        string tname = GetGeomTypeName( id );
        string spanp = FindParm( id, "Span", "XSec_1" );
        double span = spanp.size() > 0 ? GetParmVal( spanp ) : -1;
        string rotp = FindParm( id, "X_Rel_Rotation", "XForm" );
        double rot = rotp.size() > 0 ? GetParmVal( rotp ) : 0;
        string xp = FindParm( id, "X_Rel_Location", "XForm" );
        string yp = FindParm( id, "Y_Rel_Location", "XForm" );
        string zp = FindParm( id, "Z_Rel_Location", "XForm" );
        double px = xp.size() > 0 ? GetParmVal( xp ) : 0;
        double py = yp.size() > 0 ? GetParmVal( yp ) : 0;
        double pz = zp.size() > 0 ? GetParmVal( zp ) : 0;
        string tup = FindParm( id, "Tess_U", "Shape" );
        string twp = FindParm( id, "Tess_W", "Shape" );
        int tu = tup.size() > 0 ? GetParmVal( tup ) : 0;
        int tw = twp.size() > 0 ? GetParmVal( twp ) : 0;
        string denp = FindParm( id, "Density", "Mass_Props" );
        double den = denp.size() > 0 ? GetParmVal( denp ) : -1;
        string pmp = FindParm( id, "PointMass", "Mass_Props" );
        double pm = pmp.size() > 0 ? GetParmVal( pmp ) : -1;
        array<string> ss = GetSubSurfIDVec( id );
        string subs = "";
        for ( uint k = 0; k < ss.size(); k++ )
            subs += ( k > 0 ? ";" : "" ) + GetSubSurfName( id, ss[k] ) + "/" + GetSubSurfType( ss[k] );
        Print( "[GEOM] " + gname + "|" + tname + "|" + span + "|" + rot + "|"
             + px + "|" + py + "|" + pz + "|" + tu + "|" + tw + "|" + den + "|" + pm + "|" + subs + "|" + id + "\n" );
    }
    Print( "[INSPECT_OK]\n" );
}
'''


# ============================ 模板渲染 ============================

def render(template: str, mapping: dict) -> str:
    out = template.replace("{{MASS_FUNCTION}}", MASS_FUNCTION)
    for k, v in mapping.items():
        out = out.replace("{{" + k + "}}", str(v))
    if "{{" in out:
        raise ValueError(f"模板存在未替换占位符: {out[out.index('{{'):out.index('{{') + 40]}")
    return out


def render_ov_tokens(overrides: dict | None) -> dict:
    """部件覆盖 (工坊编辑) → prep 烘焙代码体。components 按几何名键:
    {"部件名": {"role": auto|wing|htail|vtail|fuselage|ignore,
                 "control": auto|none|aileron|elevator|rudder,
                 "mass_scale": 0.8, "incidence_deg": 1.5}}"""
    comps = (overrides or {}).get("components") or {}
    role_body, ctl_body, checks, apply_lines = [], [], [], []
    for name, c in comps.items():
        selector = json.dumps(name, ensure_ascii=False)
        safe = name.replace("\\", "\\\\").replace('"', '\\"').replace("\n", "\\n")
        checks.append(f'''    {{
        int matches = 0;
        for ( uint oi = 0; oi < ids.size(); oi++ )
            if ( OvMatches( ids[oi], {selector} ) ) matches++;
        if ( matches != 1 )
        {{
            Print( "[PREP_FAIL] component selector must identify one geometry: {safe}\\n" );
            return;
        }}
    }}''')
        role = (c.get("role") or "").strip()
        if role and role != "auto":
            if role not in ("wing", "htail", "vtail", "fuselage"):
                raise ValueError(f"Unknown component role: {role}")
            role_body.append(f'    if ( OvMatches( gid, {selector} ) ) return "{role}";')
        ctl = (c.get("control") or "").strip()
        if ctl and ctl != "auto":
            if ctl not in ("none", "Aileron", "Elevator", "Rudder"):
                raise ValueError(f"Unknown component control: {ctl}")
            ctl_body.append(f'    if ( OvMatches( gid, {selector} ) ) return "{ctl}";')

        ops = []
        ms = float(c.get("mass_scale") or 1.0)
        if abs(ms - 1.0) > 1e-9:
            ops.append(f'''        string dp = FindParm( gid, "Density", "Mass_Props" );
        string pp = FindParm( gid, "PointMass", "Mass_Props" );
        string ap = FindParm( gid, "Mass_Area", "Mass_Props" );
        if ( dp.size() > 0 ) SetParmValUpdate( dp, GetParmVal( dp ) * {ms} );
        if ( pp.size() > 0 ) SetParmValUpdate( pp, GetParmVal( pp ) * {ms} );
        if ( ap.size() > 0 ) SetParmValUpdate( ap, GetParmVal( ap ) * {ms} );
        Print( "  mass_scale x{ms} on {safe}\\n" );''')
        inc = float(c.get("incidence_deg") or 0.0)
        if abs(inc) > 1e-9:
            ops.append(f'''        string rp = FindParm( gid, "Y_Rel_Rotation", "XForm" );
        if ( rp.size() > 0 ) SetParmValUpdate( rp, GetParmVal( rp ) + {inc} );
        Print( "  incidence {inc}deg on {safe}\\n" );''')
        if c.get("ignore"):
            ops.append(f'''        SetSetFlag( gid, 3, false );
        SetSetFlag( gid, 1, false );   // Keep mass, exclude aerodynamic and visual surfaces.
        Print( "  ignore {safe}\\n" );''')
        if ctl and ctl != "auto":
            ops.append(f'''        array<string> ss = GetSubSurfIDVec( gid );
        for ( int k = ss.size() - 1; k >= 0; k-- ) DeleteSubSurf( gid, ss[k] );
        if ( "{ctl}" != "none" )
        {{
            string ns = AddSubSurf( gid, SS_CONTROL );
            SetSubSurfName( gid, ns, "{ctl}" );
            double e0 = 0.60, e1 = 0.95, lc = 0.25;
            if ( "{ctl}" == "Elevator" || "{ctl}" == "Rudder" ) {{ e0 = 0.10; e1 = 0.95; lc = 0.35; }}
            SetParmValUpdate( ns, "EtaFlag", "SS_Control", 1 );
            SetParmValUpdate( ns, "EtaStart", "SS_Control", e0 );
            SetParmValUpdate( ns, "EtaEnd", "SS_Control", e1 );
            SetParmValUpdate( ns, "Length_C_Start", "SS_Control", lc );
            SetParmValUpdate( ns, "Length_C_End", "SS_Control", lc );
            if ( "{ctl}" == "Elevator" || "{ctl}" == "Rudder" ) g_mirrorFix.push_back( ns );
        }}
        Print( "  control {ctl} on {safe}\\n" );''')
        if ops:
            apply_lines.append(f'''    for ( uint oi = 0; oi < ids.size(); oi++ )
    {{
        string gid = ids[oi];
        if ( !OvMatches( gid, {selector} ) ) continue;
''' + "\n".join(ops) + '''
    }''')
    return {
        "OV_ROLE_BODY": "\n".join(role_body) or "    // (no role overrides)",
        "OV_CTL_BODY": "\n".join(ctl_body) or "    // (no control overrides)",
        "OV_APPLY_BODY": "\n".join(checks + apply_lines) or "    // (no component overrides)",
    }


def sanitize_internal(name: str) -> str:
    """内部名 (目录/文件): ASCII; 中文名映射 uav_哈希。"""
    ascii_part = re.sub(r"[^A-Za-z0-9_\-]", "", name)
    if ascii_part:
        return ascii_part
    return "uav_" + hashlib.md5(name.encode("utf-8")).hexdigest()[:6]


def set_values(surface_set: str) -> tuple[int, int]:
    if surface_set == "thin":
        return SET_THIN_G, SET_THIN_T
    return SET_THICK_G, SET_THICK_T


def render_prep(src: Path, name: str, work: Path, auto_controls: bool,
                tess: int, surface_set: str, overrides: dict | None = None) -> str:
    mapping = {
        "SRC": str(src.resolve()).replace("\\", "/"),
        "NAME": name,
        "WORK": str(work.resolve()).replace("\\", "/"),
        "AUTO": "true" if auto_controls else "false",
        "TESS": tess,
    }
    mapping.update(render_ov_tokens(overrides))
    return render(PREP_TEMPLATE, mapping)


def render_case(name: str, work: Path, alpha: float, mach: float, wake: int,
                b0: float, b1: float, bn: int, a0: float, a1: float, an: int,
                surface_set: str, ref_set: str) -> str:
    gset, tset = set_values(surface_set)
    return render(CASE_TEMPLATE, {
        "NAME": name,
        "WORK": str(work.resolve()).replace("\\", "/"),
        "ALPHA": float(alpha), "MACH": float(mach), "WAKE": wake,
        "BETA_START": float(b0), "BETA_END": float(b1), "BETA_NPTS": bn,
        "ALPHA_START": float(a0), "ALPHA_END": float(a1), "ALPHA_NPTS": an,
        "GSET": gset, "TSET": tset,
        "CGSET": 4 if ref_set == "thin" else 0,
        "USE_CG": "true",   # 力矩参考位置按所选集合的 CG 模式
    })


def render_fixup(name: str, work: Path, geom_base: str, ctl_name: str,
                 fix_round: int = 0) -> str:
    # 方向舵两轮用不同展向范围 (第二轮更窄, 针对无声提取失败的垂尾)
    rud = ("0.30", "0.80") if fix_round == 0 else ("0.45", "0.65")
    return render(FIXUP_TEMPLATE, {
        "NAME": name, "WORK": str(work.resolve()).replace("\\", "/"),
        "GBASE": geom_base, "CTL": ctl_name,
        "RUD_E0": rud[0], "RUD_E1": rud[1],
    })


def parse_fail_control(log: str, case_name: str) -> tuple[str, str] | None:
    """从 case 日志解析失败的舵面: 'control surface: <案例名><GeomTag>_Surf<N>_<Name>'
    → (几何名基, 舵面名)。GeomTag 去掉布尔求交产生的 _C/_S 后缀。"""
    m = re.search(r"control surface: (\S+?)_Surf\d+_(\w+)", log)
    if not m:
        return None
    token, ctl = m.group(1), m.group(2)
    geom_tag = token[len(case_name):] if token.startswith(case_name) else token
    geom_base = re.sub(r"_[CS]$", "", geom_tag)
    if not geom_base:
        return None
    return geom_base, ctl


# ============================ 阶段执行 ============================

def run_vspscript(openvsp_dir: Path | None, script: Path, cwd: Path, log_path: Path,
                  timeout_s: int = 3600) -> tuple[int, str]:
    if openvsp_dir is None:
        import workshop_common as wc
        openvsp_dir = wc.vsproot()
    exe = openvsp_dir / "vspscript.exe"
    if not exe.is_file():
        raise FileNotFoundError(f"未找到 {exe}")
    try:
        with open(log_path, "w", encoding="utf-8", errors="replace") as log:
            proc = subprocess.run([str(exe), "-script", str(script.resolve())],
                                  cwd=str(cwd), stdout=log,
                                  stderr=subprocess.STDOUT, timeout=timeout_s)
    except subprocess.TimeoutExpired:
        # U7: 超时原样冒 traceback 会丢阶段上下文 (Unity 只见退出码 1)
        print(f"[fail] vspscript 超时 ({timeout_s}s): {script.name} — 已终止, 前段日志在 {log_path}")
        return 124, log_path.read_text(encoding="utf-8", errors="replace") if log_path.is_file() else ""
    log_text = log_path.read_text(encoding="utf-8", errors="replace")
    return proc.returncode, log_text


def parse_range(spec: str) -> tuple[float, float, int]:
    parts = [p.strip() for p in spec.split(",")]
    if len(parts) != 3:
        raise argparse.ArgumentTypeError(f"范围格式应为 起,止,点数: {spec}")
    return float(parts[0]), float(parts[1]), int(parts[2])


def grab_one(pattern: str, work: Path, what: str) -> Path:
    hits = glob.glob(str(work / pattern))
    if len(hits) != 1:
        raise RuntimeError(f"期望恰好 1 个 {what} ({pattern}), 实际 {len(hits)}: {hits}")
    return Path(hits[0])


def grab_new(pattern: str, work: Path, before: set, what: str) -> Path:
    new = {Path(p) for p in glob.glob(str(work / pattern))} - before
    if len(new) != 1:
        raise RuntimeError(f"期望恰好 1 个新增 {what} ({pattern}), 实际 {len(new)}: {sorted(new)}")
    return new.pop()


def validate_config(path: Path) -> list[str]:
    from aircraft_validation import validate_aircraft_config
    try:
        cfg = json.loads(path.read_text(encoding="utf-8"))
    except (OSError, UnicodeError, ValueError) as exc:
        return [f"AircraftConfig.json: {exc}"]
    return validate_aircraft_config(cfg)


def extract_marker(log: str, tag: str) -> str | None:
    for raw in log.splitlines():
        line = raw.strip()   # vspscript 输出行首带空格
        if line.startswith(tag):
            return line[len(tag):].strip()
    return None


def component_weight_aliases(overrides: dict, identities: dict[str, str]) -> dict:
    """Resolve UI IDs (or unambiguous legacy names) to MassProps aliases."""
    weights = {}
    for selector, values in (overrides.get("components") or {}).items():
        if "weight_kg" not in values:
            continue
        matches = ([selector[5:]] if selector.startswith("geom:")
                   and selector[5:] in identities else
                   [gid for gid, name in identities.items() if name == selector])
        if len(matches) != 1:
            raise ValueError(f"Component selector must identify one geometry: {selector}")
        weights["mass_" + matches[0]] = values["weight_kg"]
    return weights


def inspect_only(args, internal: str, out_dir: Path, work: Path) -> int:
    """部件清单 (秒级): [GEOM] 行 → components.json。"""
    as_file = work / f"inspect_{internal}.as"
    as_file.write_text(render(INSPECT_TEMPLATE, {
        "SRC": str(args.vsp.resolve()).replace("\\", "/"),
        "NAME": internal,
        "WORK": str(work.resolve()).replace("\\", "/")}),
        encoding="utf-8")
    rc, log = run_vspscript(args.openvsp_dir, as_file, work, work / "inspect.log")
    if "[INSPECT_FAIL]" in log or "[INSPECT_OK]" not in log:
        print(f"[fail] inspect 未完成 (exit={rc}), 见 {work / 'inspect.log'}")
        return 9
    comps = []
    for raw in log.splitlines():
        line = raw.strip()
        if not line.startswith("[GEOM] "):
            continue
        f = line[len("[GEOM] "):].split("|")
        if len(f) < 13:
            continue
        comps.append({
            "id": f[12], "name": f[0], "type": f[1],
            "span_m": float(f[2]), "rot_deg": float(f[3]),
            "pos_m": [float(f[4]), float(f[5]), float(f[6])],
            "tess": [int(float(f[7])), int(float(f[8]))],
            "density": float(f[9]), "point_mass_kg": float(f[10]),
            "subsurfaces": f[11],
            # 工坊可编辑字段 (默认值)
            "role": "auto", "control": "auto",
            "mass_kg": 0.0, "incidence_deg": 0.0, "ignore": False,
        })

    # Unique aliases retain identity across symmetry copies and point masses.
    from apply_overrides import parse_component_rows
    mp_rows = []
    for p in sorted(glob.glob(str(work / "*_MassProps.txt"))):
        mp_rows = parse_component_rows(Path(p))
        if mp_rows:
            break
    by_name: dict = {}
    for r in mp_rows:
        alias = r["name"].removesuffix("_pm")
        by_name[alias] = by_name.get(alias, 0.0) + r["mass"]
    for c in comps:
        c["mass_kg"] = round(by_name.get("mass_" + c["id"], 0.0), 6)

    (out_dir / "components.json").write_text(
        json.dumps(comps, ensure_ascii=False, indent=2), encoding="utf-8")
    print(f"[done] 产物: {out_dir} ({len(comps)} 个部件)")   # 与主管线同格式: 目录
    for c in comps:
        print(f"  - {c['type']}:{c['name']} span={c['span_m']} mass={c['mass_kg']}kg")
    return 0


def main(argv=None) -> int:
    ap = argparse.ArgumentParser(description="VSP 机型一键导入管线 (Phase 7)")
    ap.add_argument("--vsp", required=True, type=Path, help="上传的 .vsp3 文件")
    ap.add_argument("--name", default=None, help="机型显示名 (默认取文件名去扩展名)")
    ap.add_argument("--alpha", type=float, default=2.0, help="巡航攻角 deg")
    ap.add_argument("--mach", type=float, default=0.06, help="巡航 Mach")
    ap.add_argument("--wake", type=int, default=5, help="VSPAERO 尾涡迭代数")
    ap.add_argument("--beta-range", type=parse_range, default=(-6.0, 6.0, 5))
    ap.add_argument("--alpha-range", type=parse_range, default=(-4.0, 10.0, 7))
    ap.add_argument("--max-thrust", type=float, default=None, help="最大推力 N (默认推重比 0.35)")
    ap.add_argument("--auto-controls", action="store_true", help="无舵面时自动添加")
    ap.add_argument("--tess", type=int, default=16,
                    help="统一网格密度 (默认 16; 0=不修改)")
    ap.add_argument("--surface-set", choices=("thin", "thick"), default="thin",
                    help="网格计算集合: 薄面 thin (默认) / 厚面 thick")
    ap.add_argument("--ref-set", choices=("thin", "thick"), default="thin",
                    help="力矩参考位置集合: 薄面 thin (默认) / 厚面 thick")
    ap.add_argument("--out-dir", type=Path, default=None, help="输出目录 (默认 VspImports/<内部名>)")
    ap.add_argument("--openvsp-dir", type=Path, default=DEFAULT_OPENVSP,
                    help="含 vspscript.exe 的目录；也可用 VSPROOT 环境变量")
    ap.add_argument("--inspect-only", action="store_true",
                    help="只生成部件清单 components.json (秒级, 不跑 VSPAERO)")
    ap.add_argument("--overrides", type=Path, default=None,
                    help="覆盖 JSON (部件类别/舵面/减重/安装角/忽略 + 配重/重心/起落架)")
    args = ap.parse_args(argv)

    if not args.vsp.is_file():
        print(f"[fail] 文件不存在: {args.vsp}")
        return 1
    # F1 前置校验: 部件名含 '_' 或非 ASCII 时 VSPAERO 操纵面 tag 匹配断裂
    # (2026-10-07 e2e 实测 "C_Wing" → "Cing", 不产出 polar) — 提前明确报错
    sys.path.insert(0, str(ROOT / "Tools"))
    import workshop_common as wc
    try:
        bad = wc.bad_geom_names(wc.scan_vsp3_geom_names(args.vsp))
    except Exception:
        bad = []   # 解析失败不阻塞主流程 (prep 阶段会兜底报错)
    if bad:
        print("[fail] 部件名含 '_' 或非 ASCII, VSPAERO 操纵面匹配会断裂: "
              + ", ".join(bad) + " — 请在工坊/OpenVSP 中改名为纯字母数字后重试")
        return 1
    display = args.name or args.vsp.stem
    internal = sanitize_internal(display)
    out_dir = args.out_dir if args.out_dir is not None else ROOT / "VspImports" / internal
    work = out_dir / "work"
    work.mkdir(parents=True, exist_ok=True)
    overrides = (json.loads(args.overrides.read_text(encoding="utf-8"))
                 if args.overrides and args.overrides.is_file() else None)

    if args.inspect_only:
        return inspect_only(args, internal, out_dir, work)

    b0, b1, bn = args.beta_range
    a0, a1, an = args.alpha_range

    stages = ["prep", "STAB_DEFAULT", "BETA_SWEEP", "ALPHA_SWEEP", "gen"]
    total = len(stages)
    t0 = time.time()
    report = {"name": display, "internal": internal,
              "source": str(args.vsp.resolve()),
              "params": {"alpha": args.alpha, "mach": args.mach, "wake": args.wake,
                         "beta": list(args.beta_range), "alpha_sweep": list(args.alpha_range),
                         "max_thrust": args.max_thrust, "auto_controls": args.auto_controls,
                         "tess": args.tess, "surface_set": args.surface_set,
                         "ref_set": args.ref_set,
                         "openvsp_dir": str(args.openvsp_dir)},
              "warnings": [], "skipped_controls": []}

    def stage(i, label):
        print(f"[stage] {i}/{total} {label}", flush=True)

    # ---- Stage 1: prep ----
    stage(1, "prep")
    prep_as = work / f"import_prep_{internal}.as"
    prep_as.write_text(render_prep(args.vsp, internal, work,
                                   args.auto_controls, args.tess, args.surface_set,
                                   overrides),
                       encoding="utf-8")
    # R4: 分部件 STL 输出目录 (prep 的 ExportFile 不会自建目录)
    (work / "parts").mkdir(parents=True, exist_ok=True)
    rc, log = run_vspscript(args.openvsp_dir, prep_as, work, work / "prep.log")
    if "[PREP_FAIL]" in log:
        print(f"[fail] prep: {extract_marker(log, '[PREP_FAIL]') or 'unknown'}")
        return 2
    if "[PREP_OK]" not in log:
        print(f"[fail] prep: 无 [PREP_OK] (exit={rc}), 见 {work / 'prep.log'}")
        return 2
    identities = {}
    for ln in log.splitlines():
        if ln.strip().startswith("[COMPONENT] "):
            gid, name = ln.strip()[len("[COMPONENT] "):].split("|", 1)
            identities[gid] = name
    # R4: 分部件清单 [PART] <i> <GeomName> → parts/manifest.json (gen 阶段分组 OBJ 用)
    part_names = []
    for ln in log.splitlines():
        m = re.match(r"\s*\[PART\]\s+(\d+)\s+(.+)", ln)
        if m:
            part_names.append(m.group(2).strip())
    if part_names and (work / "parts").is_dir():
        (work / "parts" / "manifest.json").write_text(
            json.dumps([{"file": f"part_{i}.stl", "name": n}
                        for i, n in enumerate(part_names)], ensure_ascii=False, indent=1),
            encoding="utf-8")
        report["mesh_parts"] = len(part_names)
        print(f"[ok] parts: {len(part_names)} 个部件 STL")
    # Subsequent solver CG calculations may replace the normal MassProps report.
    massprops = work / "prep_massprops.txt"
    # 复用产物目录时上次的副本会被 Windows 大小写不敏感的 glob 命中, 先清掉
    massprops.unlink(missing_ok=True)
    shutil.copyfile(grab_one("*_MassProps.txt", work, "MassProps report"), massprops)
    report["mass_kg"] = float(extract_marker(log, "[MASS]") or 0)
    report["cg_m"] = [float(x) for x in (extract_marker(log, "[CG]") or "0 0 0").split()]
    report["Sref_m2"] = float(extract_marker(log, "[SREF]") or 0)
    report["bref_m"] = float(extract_marker(log, "[BREF]") or 0)
    report["cref_m"] = float(extract_marker(log, "[CREF]") or 0)
    for ln in log.splitlines():
        if ln.startswith("[PREP_WARN]"):
            report["warnings"].append(ln[len("[PREP_WARN]"):].strip())
    shutil.copy(str(work / f"{internal}_normalized.vsp3"),
                str(out_dir / f"{display}_normalized.vsp3"))
    print("[ok] prep", flush=True)

    # ---- Stage 2-4: 三工况 (独立进程 + 立即改名 + 病态舵面迁移重试) ----
    case_as = work / f"import_case_{internal}.as"
    case_as.write_text(render_case(internal, work, args.alpha, args.mach, args.wake,
                                   b0, b1, bn, a0, a1, an,
                                   args.surface_set, args.ref_set),
                       encoding="utf-8")
    renames = {"STAB_DEFAULT": ("*.stab", f"{internal}_default.stab"),
               "BETA_SWEEP": ("*.polar", f"{internal}_beta.polar"),
               "ALPHA_SWEEP": ("*.polar", f"{internal}_alpha.polar")}
    for idx, mode in enumerate(stages[1:4], start=2):
        stage(idx, mode)
        pattern, dest = renames[mode]

        for attempt in range(3):   # 病态舵面迁移重试 (最多 2 次修正)
            before = {Path(p) for p in glob.glob(str(work / pattern))}
            (work / "case.txt").write_text(mode, encoding="utf-8")
            rc, log = run_vspscript(args.openvsp_dir, case_as, work,
                                    work / f"case_{mode}.log")
            try:
                src_file = grab_new(pattern, work, before, f"{mode} 输出")
                break   # 拿到输出 (case DONE 与否以产物为准)
            except RuntimeError:
                fail_ctl = parse_fail_control(log, internal + "_normalized")
                if fail_ctl and attempt < 2:
                    geom_base, ctl = fail_ctl
                    print(f"[stage] {idx}/{total} {mode} 舵面提取失败 "
                          f"({geom_base}/{ctl}), 迁移重试 {attempt + 1}/2", flush=True)
                    fix_as = work / f"import_fix_{internal}.as"
                    fix_as.write_text(render_fixup(internal, work, geom_base, ctl),
                                      encoding="utf-8")
                    frc, flog = run_vspscript(args.openvsp_dir, fix_as, work,
                                              work / f"fix_{geom_base}_{ctl}.log")
                    if "[FIX_OK]" not in flog or "[FIX_FAIL]" in flog:
                        print(f"[fail] {mode}: 舵面迁移失败, 见 fix 日志")
                        return 7
                    report["skipped_controls"].append(f"{geom_base}.{ctl}")
                    if ctl == "Elevator":
                        report["warnings"].append(
                            f"{geom_base} 的升降舵无法提取, 已迁移到主翼内侧 (升降副翼布局)")
                    continue
                print(f"[fail] {mode}: 未产出 {pattern} (exit={rc}), "
                      f"见 {work / f'case_{mode}.log'}")
                return 3

        shutil.move(str(src_file), str(work / dest))
        content = (work / dest).read_text(encoding="utf-8", errors="replace")
        if "nan" in content.lower():
            print(f"[fail] {mode}: 求解结果含 NaN (网格退化)。建议检查模型退化/重叠面,"
                  f"或调整 --tess (当前 {args.tess}); 日志 {work / f'case_{mode}.log'}")
            return 6

        # STAB 后三轴完整性: 无声提取失败的舵面不会出现在导数表中; 组名可能带
        # 几何名前缀 (如 "WingGeom_Elevator"), 检查用 \w* 前缀匹配。
        # 缺轴 → 一轮迁移全部缺失轴后重跑 STAB (最多 2 轮)
        if mode == "STAB_DEFAULT":
            for fix_round in range(2):
                stab_text = (work / dest).read_text(encoding="utf-8", errors="replace")
                missing = [a for a in ("Aileron", "Elevator", "Rudder")
                           if not re.search(rf"^\w*{a}\b", stab_text, re.M)]
                if not missing:
                    break
                print(f"[stage] {idx}/{total} {mode} 缺 {'/'.join(missing)} 舵效, "
                      f"迁移重试 {fix_round + 1}/2", flush=True)
                for axis in missing:
                    fix_as = work / f"import_fix_{internal}.as"
                    fix_as.write_text(render_fixup(internal, work, "", axis,
                                                   fix_round),
                                      encoding="utf-8")
                    frc, flog = run_vspscript(args.openvsp_dir, fix_as, work,
                                              work / f"fix_axis_{axis}.log")
                    if "[FIX_OK]" not in flog or "[FIX_FAIL]" in flog:
                        print(f"[fail] {mode}: {axis} 迁移失败, 见 fix_axis 日志")
                        return 7
                report["skipped_controls"].append(f"relocated:{'/'.join(missing)}")
                before = {Path(p) for p in glob.glob(str(work / pattern))}
                (work / "case.txt").write_text(mode, encoding="utf-8")
                rc, log = run_vspscript(args.openvsp_dir, case_as, work,
                                        work / f"case_{mode}.log")
                try:
                    redo = grab_new(pattern, work, before, "STAB 重跑输出")
                except RuntimeError:
                    print(f"[fail] {mode}: 迁移后重跑无产出, 见 case 日志")
                    return 3
                shutil.move(str(redo), str(work / dest))
                stab_text = (work / dest).read_text(encoding="utf-8", errors="replace")
                if "nan" in stab_text.lower():
                    print(f"[fail] {mode}: 迁移重跑后含 NaN")
                    return 6
            else:
                print(f"[fail] {mode}: 迁移后仍缺舵效轴 {missing}, "
                      f"建议在 OpenVSP 中手工检查舵面子面")
                return 8
        print(f"[ok] {mode}", flush=True)

    # ---- Stage 5: 解析生成 config + obj ----
    stage(5, "gen")
    cfg_out = out_dir / "AircraftConfig.json"
    # 先在 work/ 内生成并校验, 全部成功才落到产物目录: 失败的一轮不得覆盖机型库里
    # 上一轮可用的 config (否则 config 与 model.obj/report.json 错配, 库里"换上"也会失败)
    cfg_stage = work / "AircraftConfig.json"
    gen_cmd = [sys.executable, str(TOOLS / "generate_aircraft_config.py"),
               "--name", display,
               "--massprops", str(massprops),
               "--stab", str(work / f"{internal}_default.stab"),
               "--polar", str(work / f"{internal}_alpha.polar"),
               "--beta-polar", str(work / f"{internal}_beta.polar"),
               "--mesh", f"{internal}.stl",
               "--out", str(cfg_stage)]
    if args.max_thrust is not None:
        gen_cmd += ["--max-thrust", str(args.max_thrust)]
    rc = subprocess.run(gen_cmd, cwd=str(ROOT)).returncode
    if rc != 0:
        print(f"[fail] gen: generate_aircraft_config 退出码 {rc}")
        return 4
    # 生成阶段覆盖: 部件重量重组 / 配重 / 重心指定 / 起落架自动生成
    if overrides:
        from apply_overrides import apply_overrides as _apply, parse_stl_bbox
        ov = dict(overrides)
        try:
            cw = component_weight_aliases(ov, identities)
            if cw:
                ov["component_weights"] = cw
            bbox = parse_stl_bbox(work / f"{internal}.stl")
            applied = _apply(cfg_stage, ov, bbox, massprops)
        except (ValueError, TypeError, KeyError, ZeroDivisionError) as exc:
            print(f"[fail] overrides: {exc}")
            return 5
        report["overrides_applied"] = applied
        for a in applied:
            print(f"[ok] override: {a}")

    problems = validate_config(cfg_stage)
    if problems:
        for p in problems:
            print(f"[fail] validate: {p}")
            # F2: k_induced<0 的实测主因是粗机身作薄面导致阻力解非物理 — 给出可操作的指引
            if "k_induced" in p:
                print("[fail] 提示: 诱导阻力因子为负通常意味着极曲线非物理 — 常见原因是"
                      "粗机身(直径/机长>0.1)作为薄面参与计算。请在工坊中减小机身直径,"
                      "或把机身移出薄面集合后重试。")
        return 5
    obj_out = out_dir / "model.obj"
    obj_stage = work / "model.obj"
    parts_arg = []
    if (work / "parts" / "manifest.json").is_file():
        parts_arg = ["--parts", str(work / "parts")]
    rc = subprocess.run([sys.executable, str(TOOLS / "stl_to_obj_unity.py"),
                         str(work / f"{internal}.stl"), str(obj_stage),
                         "--config", str(cfg_stage)] + parts_arg, cwd=str(ROOT)).returncode
    if rc != 0:
        print(f"[fail] gen: stl_to_obj_unity 退出码 {rc}")
        return 4
    shutil.copyfile(cfg_stage, cfg_out)
    shutil.copyfile(obj_stage, obj_out)
    report["warnings"].append(
        "meta.design 为生成器默认常数 (性能面板尾翼估算不准, 不影响飞行)")
    report["config"] = str(cfg_out)
    report["obj"] = str(obj_out)
    report["normalized_vsp3"] = str(out_dir / f"{display}_normalized.vsp3")
    report["duration_s"] = round(time.time() - t0, 1)
    (out_dir / "report.json").write_text(
        json.dumps(report, ensure_ascii=False, indent=2), encoding="utf-8")
    print("[ok] gen")
    print(f"[done] 产物: {out_dir} (用时 {report['duration_s']}s)")
    return 0


if __name__ == "__main__":
    sys.exit(main())
