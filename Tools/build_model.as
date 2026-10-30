// ============================================================
// OpenVSP 飞行模拟器项目 — Phase 0 第 1 步: 建模 + 质量属性
//
// 由 Tools/run_all_analyses.py 调用 (CWD = Data/):
//   1. 构建 TestUAV (POD 机身 + 主翼 + 平尾 + 垂尾 + 舵面)
//   2. MassProp 质量属性分析 -> TestUAV_MassProps.txt
//   3. 用计算重心设置 VSPAERO Xcg/Ycg/Zcg
//   4. 恢复几何显示状态 (MassProp 会 HideAllExcept, 必须撤销, 见下)
//   5. 保存 TestUAV.vsp3 + 导出 TestUAV.stl
//
// 坐标系: OpenVSP 惯例 — X 向后, Y 向右, Z 向上 (米)
// 质量方案: WING 是零厚度开放曲面, MassProp 封腔会虚增体积 ->
//   机身 POD (闭合实体) 用体积密度; 翼面用 Density=1 + PointMass 点质量
//   (注意: 点质量 CG 相对几何原点; 对称翼的点质量会镜像双计 -> 设半量)
// ============================================================

const string MODEL_NAME = "TestUAV";

void main()
{
    Print( "=== Step 1: build model + MassProp ===\n" );

    // ---- 机身 POD: 长 2.8m, 细长比 19 (半径=L/FR=0.147m), 体积密度 80 ----
    string pod = AddGeom( "POD" );
    SetGeomName( pod, "Fuselage" );
    SetParmValUpdate( pod, "Length", "Design", 2.8 );
    SetParmValUpdate( pod, "FineRatio", "Design", 19.0 );
    SetParmValUpdate( pod, "X_Rel_Location", "XForm", 0.0 );
    SetParmValUpdate( pod, "Z_Rel_Location", "XForm", 0.0 );
    SetParmValUpdate( pod, "Density", "Mass_Props", 80.0 );
    // 电池点质量 (CG 相对本几何原点; POD 原点在 x=0)
    SetParmValUpdate( pod, "PointMass", "Mass_Props", 2.2 );
    SetParmValUpdate( pod, "CGx", "Mass_Props", 1.90 );
    SetParmValUpdate( pod, "CGy", "Mass_Props", 0.0 );
    SetParmValUpdate( pod, "CGz", "Mass_Props", 0.05 );

    // ---- 主翼 WING: 展长 4.0m (半展 2.0), 根弦 1.0, 尖弦 0.55 (原构型根 0.55/尖 1.0 的互换), 上反 3 度 ----
    // 注意: XSec 驱动参数是 (Span, Root_Chord, Tip_Chord), Taper 是被驱动的派生量,
    // SetParmValUpdate "Taper" 会被静默忽略 — 必须直接设 Tip_Chord。
    string wing = AddGeom( "WING" );
    SetGeomName( wing, "MainWing" );
    SetParmValUpdate( wing, "Root_Chord", "XSec_1", 1.0 );
    SetParmValUpdate( wing, "Tip_Chord", "XSec_1", 0.55 );
    SetParmValUpdate( wing, "Span", "XSec_1", 2.0 );
    SetParmValUpdate( wing, "Sweep", "XSec_1", 1.0 );
    SetParmValUpdate( wing, "Sweep_Location", "XSec_1", 0.25 );
    SetParmValUpdate( wing, "Dihedral", "XSec_1", 3.0 );
    SetParmValUpdate( wing, "X_Rel_Location", "XForm", 1.30 );
    SetParmValUpdate( wing, "Z_Rel_Location", "XForm", 0.16 );
    SetParmValUpdate( wing, "Density", "Mass_Props", 1.0 );
    // 机翼结构点质量 (镜像 x2 = 2.6 kg); CG 相对机翼原点 (1.30, 0, 0.16)
    SetParmValUpdate( wing, "PointMass", "Mass_Props", 1.3 );
    SetParmValUpdate( wing, "CGx", "Mass_Props", 0.28 );
    SetParmValUpdate( wing, "CGy", "Mass_Props", 0.0 );
    SetParmValUpdate( wing, "CGz", "Mass_Props", 0.03 );
    // 副翼 (外翼段后缘, 展向 60%~95%, 弦向 25%)
    string aileron = AddSubSurf( wing, SS_CONTROL );
    SetSubSurfName( wing, aileron, "Aileron" );
    SetParmValUpdate( aileron, "EtaFlag", "SS_Control", 1 );
    SetParmValUpdate( aileron, "EtaStart", "SS_Control", 0.60 );
    SetParmValUpdate( aileron, "EtaEnd", "SS_Control", 0.95 );
    SetParmValUpdate( aileron, "Length_C_Start", "SS_Control", 0.25 );
    SetParmValUpdate( aileron, "Length_C_End", "SS_Control", 0.25 );

    // ---- 平尾 WING: 半展 0.7, 根弦 1.0, 尖弦 0.30 (原构型根 0.30/尖 1.0 的互换) ----
    string htail = AddGeom( "WING" );
    SetGeomName( htail, "HTail" );
    SetParmValUpdate( htail, "Root_Chord", "XSec_1", 1.0 );
    SetParmValUpdate( htail, "Tip_Chord", "XSec_1", 0.30 );
    SetParmValUpdate( htail, "Span", "XSec_1", 0.7 );
    SetParmValUpdate( htail, "Sweep", "XSec_1", 2.0 );
    SetParmValUpdate( htail, "Sweep_Location", "XSec_1", 0.25 );
    SetParmValUpdate( htail, "X_Rel_Location", "XForm", 2.45 );
    SetParmValUpdate( htail, "Z_Rel_Location", "XForm", 0.18 );
    SetParmValUpdate( htail, "Density", "Mass_Props", 1.0 );
    // 平尾结构点质量 (镜像 x2 = 0.45 kg); CG 相对平尾原点 (2.45, 0, 0.18)
    SetParmValUpdate( htail, "PointMass", "Mass_Props", 0.225 );
    SetParmValUpdate( htail, "CGx", "Mass_Props", 0.15 );
    SetParmValUpdate( htail, "CGy", "Mass_Props", 0.0 );
    SetParmValUpdate( htail, "CGz", "Mass_Props", 0.01 );
    // 升降舵 (展向 10%~95%, 弦向 35%)
    string elev = AddSubSurf( htail, SS_CONTROL );
    SetSubSurfName( htail, elev, "Elevator" );
    SetParmValUpdate( elev, "EtaFlag", "SS_Control", 1 );
    SetParmValUpdate( elev, "EtaStart", "SS_Control", 0.10 );
    SetParmValUpdate( elev, "EtaEnd", "SS_Control", 0.95 );
    SetParmValUpdate( elev, "Length_C_Start", "SS_Control", 0.35 );
    SetParmValUpdate( elev, "Length_C_End", "SS_Control", 0.35 );

    // ---- 垂尾 WING (旋转 90 度): 高 0.55, 根弦 1.0, 尖弦 0.35 (原构型根 0.35/尖 1.0 的互换), 后掠 25 度 ----
    string vtail = AddGeom( "WING" );
    SetGeomName( vtail, "VTail" );
    SetParmValUpdate( vtail, "Root_Chord", "XSec_1", 1.0 );
    SetParmValUpdate( vtail, "Tip_Chord", "XSec_1", 0.35 );
    SetParmValUpdate( vtail, "Span", "XSec_1", 0.55 );
    SetParmValUpdate( vtail, "Sweep", "XSec_1", 25.0 );
    SetParmValUpdate( vtail, "Sweep_Location", "XSec_1", 0.25 );
    SetParmValUpdate( vtail, "X_Rel_Location", "XForm", 2.45 );
    SetParmValUpdate( vtail, "Z_Rel_Location", "XForm", 0.18 );
    SetParmValUpdate( vtail, "X_Rel_Rotation", "XForm", 90 );
    SetParmValUpdate( vtail, "Sym_Planar_Flag", "Sym", 0 );  // 垂尾不做镜像
    SetParmValUpdate( vtail, "Density", "Mass_Props", 1.0 );
    // 垂尾结构点质量 (无镜像, 全量 0.25 kg); 旋转后局部 CGy 对应世界 z 向
    SetParmValUpdate( vtail, "PointMass", "Mass_Props", 0.25 );
    SetParmValUpdate( vtail, "CGx", "Mass_Props", 0.20 );
    SetParmValUpdate( vtail, "CGy", "Mass_Props", 0.22 );
    SetParmValUpdate( vtail, "CGz", "Mass_Props", 0.0 );
    // 方向舵 (展向 10%~95%, 弦向 35%)
    string rudder = AddSubSurf( vtail, SS_CONTROL );
    SetSubSurfName( vtail, rudder, "Rudder" );
    SetParmValUpdate( rudder, "EtaFlag", "SS_Control", 1 );
    SetParmValUpdate( rudder, "EtaStart", "SS_Control", 0.10 );
    SetParmValUpdate( rudder, "EtaEnd", "SS_Control", 0.95 );
    SetParmValUpdate( rudder, "Length_C_Start", "SS_Control", 0.35 );
    SetParmValUpdate( rudder, "Length_C_End", "SS_Control", 0.35 );

    // ---- 参考机翼 + 舵面分组 ----
    SetVSPAERORefWingID( wing );          // 自动填 Sref/bref/cref
    AutoGroupVSPAEROControlSurfaces();    // 生成 ControlSurfaceGroup x3

    // 关键: 镜像舵面对在默认 +1/+1 增益下反对称偏转 (适合副翼, 不适合升降舵).
    // 升降舵需两侧同向偏转 -> 把镜像侧 (_1) 增益取反.
    // 否则 .stab 中升降舵只产生滚转力矩, 俯仰操纵效能几乎为零.
    {
        string vsp_set0 = FindContainer( "VSPAEROSettings", 0 );
        int ngrp = GetNumControlSurfaceGroups();
        bool fixed_elev = false;
        for ( int gi = 0; gi <= ngrp && !fixed_elev; gi++ )
        {
            string grp = "ControlSurfaceGroup_" + gi;
            string g1 = FindParm( vsp_set0, "Surf_" + elev + "_1_Gain", grp );
            if ( g1.size() > 0 )
            {
                SetParmValUpdate( g1, -1.0 );
                Print( "Elevator mirror gain (" + grp + ") set to -1\n" );
                fixed_elev = true;
            }
        }
        if ( !fixed_elev )
        {
            Print( "WARNING: 未找到升降舵镜像增益参数!\n" );
        }
    }

    Print( "NumControlSurfaceGroups = " + GetNumControlSurfaceGroups() + "\n" );
    Update();

    string vsp_set = FindContainer( "VSPAEROSettings", 0 );
    Print( "Sref = " + GetParmVal( FindParm( vsp_set, "Sref", "VSPAERO" ) ) +
           "  bref = " + GetParmVal( FindParm( vsp_set, "bref", "VSPAERO" ) ) +
           "  cref = " + GetParmVal( FindParm( vsp_set, "cref", "VSPAERO" ) ) + "\n" );

    // ---- 保存 (先用初始名, 使导出文件名有正确基础) ----
    WriteVSPFile( MODEL_NAME + ".vsp3", SET_ALL );

    // ---- MassProp 质量属性分析 ----
    Print( "\n--- ExecAnalysis: MassProp ---\n" );
    SetAnalysisInputDefaults( "MassProp" );
    string mp_res = ExecAnalysis( "MassProp" );

    // 删除 MassProp 创建的临时网格几何 (不删会污染后续流程/存档)
    array<string> mp_mesh_ids = GetStringResults( mp_res, "Mesh_GeomID" );
    DeleteGeomVec( mp_mesh_ids );

    array<vec3d> cg_arr = GetVec3dResults( mp_res, "Total_CG" );
    array<double> mass_arr = GetDoubleResults( mp_res, "Total_Mass" );
    Print( "Total_Mass = " + mass_arr[0] + " kg\n" );
    Print( "Total_CG   = ( " + cg_arr[0].x() + ", " + cg_arr[0].y() + ", " + cg_arr[0].z() + " )\n" );

    // ---- VSPAERO 力矩参考点 = 实际重心 ----
    SetParmValUpdate( vsp_set, "Xcg", "VSPAERO", cg_arr[0].x() );
    SetParmValUpdate( vsp_set, "Ycg", "VSPAERO", cg_arr[0].y() );
    SetParmValUpdate( vsp_set, "Zcg", "VSPAERO", cg_arr[0].z() );

    // ---- 关键: 恢复被 MassProp 隐藏的几何 (SET_SHOWN = 1) ----
    // MassProp 内部 HideAllExcept 且隐藏状态会存进 vsp3;
    // 后续进程读取该文件做 VSPAEROComputeGeometry 时, 隐藏几何不参与
    // 网格生成 -> .vspgeom 为空 -> vspaero 报 "无法加载 VSPGEOM 文件"
    SetSetFlag( pod,   1, true );
    SetSetFlag( wing,  1, true );
    SetSetFlag( htail, 1, true );
    SetSetFlag( vtail, 1, true );

    Update();

    // ---- 最终保存 + STL 导出 (Unity 渲染网格) ----
    WriteVSPFile( MODEL_NAME + ".vsp3", SET_ALL );
    ExportFile( MODEL_NAME + ".stl", 0, EXPORT_STL );
    Print( "Saved " + MODEL_NAME + ".vsp3 + " + MODEL_NAME + ".stl\n" );
    Print( "=== Step 1 DONE ===\n" );
}
