// ============================================================
// OpenVSP 飞行模拟器项目 — Phase 0 数据生成脚本 (AngelScript)
//
// 用途: 通过 vspscript.exe 批处理执行
//   1. 构建 TestUAV 测试机 (POD 机身 + 主翼 + 平尾 + 垂尾 + 舵面)
//   2. 保存 TestUAV.vsp3
//   3. 运行 MassProp 质量属性分析 -> TestUAV_MassProps.txt
//      并用计算出的重心自动设置 VSPAERO 的 Xcg/Ycg/Zcg (保持一致)
//   4. 运行 VSPAERO 稳定性分析 x4 (DEFAULT/P/Q/R)
//      -> TestUAV.stab / .pstab / .qstab / .rstab
//   5. 运行 beta 扫描 + alpha 扫描 (STABILITY_OFF) -> TestUAV.polar
//      (用于诱导阻力 CD = CD0 + k*CL^2 拟合)
//   6. 导出 STL 网格 (Unity 渲染用)
//
// 运行方式 (CWD 必须是数据输出目录, 即本项目的 Data/):
//   cd openvsp-flightsim-dev/Data
//   <OpenVSP安装目录>/vspscript.exe -script ../Tools/build_and_analyze.as
//
// 坐标系: OpenVSP 惯例 — X 向后, Y 向右, Z 向上 (单位: 米)
//
// 质量方案说明: OpenVSP 的 WING 几何是零厚度开放曲面, MassProp 会把开放网格
// 合并封腔导致体积严重膨胀 (实测虚增 ~90 kg)。因此:
//   - 机身 POD (闭合实体) 用体积密度 150 kg/m^3
//   - 机翼/平尾/垂尾用 Density=1.0 (虚增质量 <1 kg) + PointMass 点质量
// ============================================================

const string MODEL_NAME = "TestUAV";

// ---- VSPAERO 分析公共参数 ----
const double CRUISE_ALPHA = 2.0;   // 巡航攻角 (deg)
const double CRUISE_MACH  = 0.06;  // ~20 m/s 海平面
const int    WAKE_ITER    = 5;

// ---- 稳定性分析类型枚举 (vsp::VSPAERO_STABILITY_TYPE) ----
const int STAB_OFF     = 0;
const int STAB_DEFAULT = 1;  // 稳态 6DOF -> .stab
const int STAB_P       = 2;  // 滚转率   -> .pstab
const int STAB_Q       = 3;  // 俯仰率   -> .qstab
const int STAB_R       = 4;  // 偏航率   -> .rstab

void main()
{
    Print( "=== OpenVSP Flightsim Phase 0: build & analyze ===\n" );

    // ============ 1. 构建几何 ============
    // ---- 机身 POD: 长 2.8m, 细长比 19 (半径=L/FR=0.147m, 直径 0.295m), 体积密度 80 ----
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

    // ---- 主翼 WING: 展长 4.0m (半展 2.0), 根弦 0.55, 锥比 0.55, 上反 3 度 ----
    string wing = AddGeom( "WING" );
    SetGeomName( wing, "MainWing" );
    SetParmValUpdate( wing, "Root_Chord", "XSec_1", 0.55 );
    SetParmValUpdate( wing, "Taper", "XSec_1", 0.55 );
    SetParmValUpdate( wing, "Span", "XSec_1", 2.0 );
    SetParmValUpdate( wing, "Sweep", "XSec_1", 1.0 );
    SetParmValUpdate( wing, "Sweep_Location", "XSec_1", 0.25 );
    SetParmValUpdate( wing, "Dihedral", "XSec_1", 3.0 );
    SetParmValUpdate( wing, "X_Rel_Location", "XForm", 1.30 );
    SetParmValUpdate( wing, "Z_Rel_Location", "XForm", 0.16 );
    SetParmValUpdate( wing, "Density", "Mass_Props", 1.0 );
    // 机翼结构点质量: 对称机翼的点质量会被镜像复制 -> 设半量 (1.3 x2 = 2.6 kg)
    // CG 相对机翼原点 (根弦前缘, 绝对位置 x=1.30, z=0.16)
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

    // ---- 平尾 WING: 半展 0.7, 根弦 0.30, 锥比 0.7 ----
    string htail = AddGeom( "WING" );
    SetGeomName( htail, "HTail" );
    SetParmValUpdate( htail, "Root_Chord", "XSec_1", 0.30 );
    SetParmValUpdate( htail, "Taper", "XSec_1", 0.7 );
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

    // ---- 垂尾 WING (旋转 90 度): 高 0.55, 根弦 0.35, 锥比 0.6, 后掠 25 度 ----
    string vtail = AddGeom( "WING" );
    SetGeomName( vtail, "VTail" );
    SetParmValUpdate( vtail, "Root_Chord", "XSec_1", 0.35 );
    SetParmValUpdate( vtail, "Taper", "XSec_1", 0.6 );
    SetParmValUpdate( vtail, "Span", "XSec_1", 0.55 );
    SetParmValUpdate( vtail, "Sweep", "XSec_1", 25.0 );
    SetParmValUpdate( vtail, "Sweep_Location", "XSec_1", 0.25 );
    SetParmValUpdate( vtail, "X_Rel_Location", "XForm", 2.45 );
    SetParmValUpdate( vtail, "Z_Rel_Location", "XForm", 0.18 );
    SetParmValUpdate( vtail, "X_Rel_Rotation", "XForm", 90 );
    SetParmValUpdate( vtail, "Sym_Planar_Flag", "Sym", 0 );  // 垂尾不做镜像
    SetParmValUpdate( vtail, "Density", "Mass_Props", 1.0 );
    // 垂尾结构点质量 (无镜像, 全量 0.25 kg); 旋转 90 度后局部 CGy 对应世界 z 向
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

    // ============ 2. 参考量 ============
    // 主翼设为 VSPAERO 参考机翼 (自动填 Sref/bref/cref)
    SetVSPAERORefWingID( wing );

    // 自动把舵面子面分组为 ControlSurfaceGroup
    AutoGroupVSPAEROControlSurfaces();
    Print( "NumControlSurfaceGroups = " + GetNumControlSurfaceGroups() + "\n" );

    Update();

    // 打印参考量 (供核对)
    string vsp_set = FindContainer( "VSPAEROSettings", 0 );
    Print( "Sref = " + GetParmVal( FindParm( vsp_set, "Sref", "VSPAERO" ) ) + "\n" );
    Print( "bref = " + GetParmVal( FindParm( vsp_set, "bref", "VSPAERO" ) ) + "\n" );
    Print( "cref = " + GetParmVal( FindParm( vsp_set, "cref", "VSPAERO" ) ) + "\n" );

    // ============ 3. 保存模型 ============
    WriteVSPFile( MODEL_NAME + ".vsp3", SET_ALL );
    Print( "Saved " + MODEL_NAME + ".vsp3\n" );

    // ============ 4. 质量属性分析 -> 用实际重心设置 VSPAERO Xcg ============
    Print( "\n--- ExecAnalysis: MassProp ---\n" );
    SetAnalysisInputDefaults( "MassProp" );
    string mp_res = ExecAnalysis( "MassProp" );

    // 关键: 删除 MassProp 创建的临时网格几何.
    // 若不删除, 残留的网格几何会混入后续 VSPAEROComputeGeometry 的
    // 网格求交 (GeomSet=-1 表示全部几何), 导致 .vspgeom 生成失败.
    array<string> mp_mesh_ids = GetStringResults( mp_res, "Mesh_GeomID" );
    DeleteGeomVec( mp_mesh_ids );

    // 关键: MassProp 分析内部会 HideAllExcept 隐藏所有原始几何,
    // 且隐藏状态会持久化 (存进 vsp3 / 影响后续分析).
    // CompGeom 只对 "已显示" (SET_SHOWN=1) 的几何建网格,
    // 因此必须在这里恢复全部几何的显示状态, 否则 vspgeom 为空.
    SetSetFlag( pod,   1, true );   // SET_SHOWN
    SetSetFlag( wing,  1, true );
    SetSetFlag( htail, 1, true );
    SetSetFlag( vtail, 1, true );

    array<vec3d> cg_arr = GetVec3dResults( mp_res, "Total_CG" );
    array<double> mass_arr = GetDoubleResults( mp_res, "Total_Mass" );
    Print( "Total_Mass = " + mass_arr[0] + " kg\n" );
    Print( "Total_CG   = ( " + cg_arr[0].x() + ", " + cg_arr[0].y() + ", " + cg_arr[0].z() + " )\n" );

    // VSPAERO 力矩参考点 = 实际重心 (保证 .stab 导数与质量属性一致)
    SetParmValUpdate( vsp_set, "Xcg", "VSPAERO", cg_arr[0].x() );
    SetParmValUpdate( vsp_set, "Ycg", "VSPAERO", cg_arr[0].y() );
    SetParmValUpdate( vsp_set, "Zcg", "VSPAERO", cg_arr[0].z() );
    Update();

    // ============ 5. VSPAERO 稳定性分析 (4 种) ============
    RunVSPAEROStability( STAB_DEFAULT, "DEFAULT (.stab)" );
    RunVSPAEROStability( STAB_P, "ROLL P (.pstab)" );
    RunVSPAEROStability( STAB_Q, "PITCH Q (.qstab)" );
    RunVSPAEROStability( STAB_R, "YAW R (.rstab)" );

    // ============ 6. beta 扫描 (STABILITY_OFF) ============
    Print( "\n--- VSPAERO beta sweep ---\n" );
    RunVSPAEROSweep( -6.0, 5, CRUISE_ALPHA, 1, STAB_OFF );

    // ============ 7. alpha 扫描 (STABILITY_OFF, 最后运行以保留 .polar) ============
    Print( "\n--- VSPAERO alpha sweep ---\n" );
    RunVSPAEROSweep( -4.0, 7, 0.0, 0, STAB_OFF );

    // ============ 8. 导出 STL ============
    Print( "\n--- Export STL ---\n" );
    ExportFile( MODEL_NAME + ".stl", 0, EXPORT_STL );
    Print( "Exported " + MODEL_NAME + ".stl\n" );

    Print( "\n=== ALL DONE ===\n" );
}

// 运行一次 VSPAERO 稳定性分析
void RunVSPAEROStability( int stab_type, string label )
{
    Print( "\n--- VSPAERO Stability: " + label + " ---\n" );

    SetAnalysisInputDefaults( "VSPAEROSweep" );

    array<int> geom_set;  geom_set.push_back( 0 );
    SetIntAnalysisInput( "VSPAEROSweep", "GeomSet", geom_set );

    array<int> ref_flag;  ref_flag.push_back( 3 );  // 使用 Sref/bref/cref 输入值
    SetIntAnalysisInput( "VSPAEROSweep", "RefFlag", ref_flag );

    array<double> alpha;  alpha.push_back( CRUISE_ALPHA );
    SetDoubleAnalysisInput( "VSPAEROSweep", "AlphaStart", alpha );
    array<int> alpha_npts;  alpha_npts.push_back( 1 );
    SetIntAnalysisInput( "VSPAEROSweep", "AlphaNpts", alpha_npts );

    array<double> beta;  beta.push_back( 0.0 );
    SetDoubleAnalysisInput( "VSPAEROSweep", "BetaStart", beta );
    array<int> beta_npts;  beta_npts.push_back( 1 );
    SetIntAnalysisInput( "VSPAEROSweep", "BetaNpts", beta_npts );

    array<double> mach;  mach.push_back( CRUISE_MACH );
    SetDoubleAnalysisInput( "VSPAEROSweep", "MachStart", mach );
    array<int> mach_npts;  mach_npts.push_back( 1 );
    SetIntAnalysisInput( "VSPAEROSweep", "MachNpts", mach_npts );

    array<int> wake;  wake.push_back( WAKE_ITER );
    SetIntAnalysisInput( "VSPAEROSweep", "WakeNumIter", wake );

    array<int> stab;  stab.push_back( stab_type );
    SetIntAnalysisInput( "VSPAEROSweep", "UnsteadyType", stab );

    Update();

    // 关键: VSPAEROSweep 分析本身不生成网格, 必须先执行 ComputeGeometry
    // 生成 .vspgeom 气动数据库 (否则 vspaero.exe 报 "无法加载 VSPGEOM 文件")
    SetAnalysisInputDefaults( "VSPAEROComputeGeometry" );
    string gid = ExecAnalysis( "VSPAEROComputeGeometry" );
    Print( "  geometry id: " + gid + "\n" );

    string rid = ExecAnalysis( "VSPAEROSweep" );
    Print( "  " + label + " results id: " + rid + "\n" );
}

// 运行一次 VSPAERO 参数扫描
// beta_mode=1: 扫 beta (scan_start 起 npts 点), alpha 固定为 fixed_alpha
// beta_mode=0: 扫 alpha (scan_start 起 npts 点), beta 固定 0
void RunVSPAEROSweep( double scan_start, int npts, double fixed_alpha, int beta_mode, int stab_type )
{
    SetAnalysisInputDefaults( "VSPAEROSweep" );

    array<int> geom_set;  geom_set.push_back( 0 );
    SetIntAnalysisInput( "VSPAEROSweep", "GeomSet", geom_set );

    array<int> ref_flag;  ref_flag.push_back( 3 );
    SetIntAnalysisInput( "VSPAEROSweep", "RefFlag", ref_flag );

    array<double> mach;  mach.push_back( CRUISE_MACH );
    SetDoubleAnalysisInput( "VSPAEROSweep", "MachStart", mach );
    array<int> mach_npts;  mach_npts.push_back( 1 );
    SetIntAnalysisInput( "VSPAEROSweep", "MachNpts", mach_npts );

    array<int> wake;  wake.push_back( WAKE_ITER );
    SetIntAnalysisInput( "VSPAEROSweep", "WakeNumIter", wake );

    array<int> stab;  stab.push_back( stab_type );
    SetIntAnalysisInput( "VSPAEROSweep", "UnsteadyType", stab );

    if ( beta_mode == 1 )
    {
        array<double> alpha;  alpha.push_back( fixed_alpha );
        SetDoubleAnalysisInput( "VSPAEROSweep", "AlphaStart", alpha );
        array<int> alpha_npts;  alpha_npts.push_back( 1 );
        SetIntAnalysisInput( "VSPAEROSweep", "AlphaNpts", alpha_npts );

        array<double> beta;  beta.push_back( scan_start );
        SetDoubleAnalysisInput( "VSPAEROSweep", "BetaStart", beta );
        array<int> beta_npts;  beta_npts.push_back( npts );
        SetIntAnalysisInput( "VSPAEROSweep", "BetaNpts", beta_npts );
    }
    else
    {
        array<double> alpha;  alpha.push_back( scan_start );
        SetDoubleAnalysisInput( "VSPAEROSweep", "AlphaStart", alpha );
        array<int> alpha_npts;  alpha_npts.push_back( npts );
        SetIntAnalysisInput( "VSPAEROSweep", "AlphaNpts", alpha_npts );

        array<double> beta;  beta.push_back( 0.0 );
        SetDoubleAnalysisInput( "VSPAEROSweep", "BetaStart", beta );
        array<int> beta_npts;  beta_npts.push_back( 1 );
        SetIntAnalysisInput( "VSPAEROSweep", "BetaNpts", beta_npts );
    }

    Update();

    // 生成网格数据库 (见 RunVSPAEROStability 中的说明)
    SetAnalysisInputDefaults( "VSPAEROComputeGeometry" );
    string gid = ExecAnalysis( "VSPAEROComputeGeometry" );
    Print( "  geometry id: " + gid + "\n" );

    string rid = ExecAnalysis( "VSPAEROSweep" );
    Print( "  sweep results id: " + rid + "\n" );
}
