// ============================================================
// OpenVSP 飞行模拟器项目 — Phase 0 第 2 步: 单工况 VSPAERO 分析
//
// 由 Tools/run_all_analyses.py 生成 case.txt 后调用 (CWD = Data/):
//   case.txt 内容为一行模式名:
//     STAB_DEFAULT -> 运行稳态稳定性分析, 产出 TestUAV.stab
//     STAB_P / STAB_Q / STAB_R -> 非定常滚转/俯仰/偏航, 产出 .pstab/.qstab/.rstab
//     ALPHA_SWEEP  -> alpha 扫描 -4..10 度 x7 (beta=0), 产出 .polar
//     BETA_SWEEP   -> beta 扫描 -6..6 度 x5 (alpha=2), 产出 .polar
//
// 为什么每个工况单独一次 vspscript 进程:
//   ComputeSolver 每次运行前会删除上一轮的全部输出文件 (.stab/.polar/
//   .history/.polar 等), 单进程连续跑会互相覆盖 -> 由 Python 驱动器在
//   每个进程结束后立即把输出改名保全。
// ============================================================

const string MODEL_NAME = "TestUAV";
const double CRUISE_ALPHA = 2.0;   // 巡航攻角 (deg)
const double CRUISE_MACH  = 0.06;  // ~20 m/s 海平面
const int    WAKE_ITER    = 5;

const int STAB_OFF     = 0;
const int STAB_DEFAULT = 1;
const int STAB_P       = 2;
const int STAB_Q       = 3;
const int STAB_R       = 4;

void main()
{
    // ---- 读取工况模式 ----
    string mode = "UNKNOWN";
    {
        file@ f = file();
        if ( f.open( "case.txt", "r" ) >= 0 )
        {
            if ( !f.isEndOfFile() )
                mode = f.readLine();
            f.close();
        }
    }
    Print( "=== Step 2: VSPAERO case mode=" + mode + " ===\n" );

    // ---- 读取第 1 步保存的模型 ----
    ReadVSPFile( MODEL_NAME + ".vsp3" );

    // 控制面分组需要重建 (保证 .stab 含操纵导数列)
    AutoGroupVSPAEROControlSurfaces();

    string vsp_set = FindContainer( "VSPAEROSettings", 0 );
    Print( "Xcg = " + GetParmVal( FindParm( vsp_set, "Xcg", "VSPAERO" ) ) + "\n" );

    Update();

    // ---- 公共分析输入 ----
    SetAnalysisInputDefaults( "VSPAEROSweep" );

    array<int> geom_set;  geom_set.push_back( 0 );
    SetIntAnalysisInput( "VSPAEROSweep", "GeomSet", geom_set );

    array<int> ref_flag;  ref_flag.push_back( 3 );  // 手动 Sref/bref/cref (已在模型里)
    SetIntAnalysisInput( "VSPAEROSweep", "RefFlag", ref_flag );

    array<double> mach;  mach.push_back( CRUISE_MACH );
    SetDoubleAnalysisInput( "VSPAEROSweep", "MachStart", mach );
    array<int> mach_npts;  mach_npts.push_back( 1 );
    SetIntAnalysisInput( "VSPAEROSweep", "MachNpts", mach_npts );

    array<int> wake;  wake.push_back( WAKE_ITER );
    SetIntAnalysisInput( "VSPAEROSweep", "WakeNumIter", wake );

    // ---- 按模式设置稳定性类型与扫描变量 ----
    if ( mode == "STAB_DEFAULT" || mode == "STAB_P" || mode == "STAB_Q" || mode == "STAB_R" )
    {
        int stab_type = STAB_DEFAULT;
        if ( mode == "STAB_P" )       stab_type = STAB_P;
        else if ( mode == "STAB_Q" )  stab_type = STAB_Q;
        else if ( mode == "STAB_R" )  stab_type = STAB_R;

        array<double> alpha;  alpha.push_back( CRUISE_ALPHA );
        SetDoubleAnalysisInput( "VSPAEROSweep", "AlphaStart", alpha );
        array<int> alpha_npts;  alpha_npts.push_back( 1 );
        SetIntAnalysisInput( "VSPAEROSweep", "AlphaNpts", alpha_npts );

        array<double> beta;  beta.push_back( 0.0 );
        SetDoubleAnalysisInput( "VSPAEROSweep", "BetaStart", beta );
        array<int> beta_npts;  beta_npts.push_back( 1 );
        SetIntAnalysisInput( "VSPAEROSweep", "BetaNpts", beta_npts );

        array<int> stab;  stab.push_back( stab_type );
        SetIntAnalysisInput( "VSPAEROSweep", "UnsteadyType", stab );
    }
    else if ( mode == "BETA_SWEEP" )
    {
        array<double> alpha;  alpha.push_back( CRUISE_ALPHA );
        SetDoubleAnalysisInput( "VSPAEROSweep", "AlphaStart", alpha );
        array<int> alpha_npts;  alpha_npts.push_back( 1 );
        SetIntAnalysisInput( "VSPAEROSweep", "AlphaNpts", alpha_npts );

        array<double> beta;  beta.push_back( -6.0 );   // -6 到 +6, 5 点
        SetDoubleAnalysisInput( "VSPAEROSweep", "BetaStart", beta );
        array<int> beta_npts;  beta_npts.push_back( 5 );
        SetIntAnalysisInput( "VSPAEROSweep", "BetaNpts", beta_npts );

        array<int> stab;  stab.push_back( STAB_OFF );
        SetIntAnalysisInput( "VSPAEROSweep", "UnsteadyType", stab );
    }
    else  // ALPHA_SWEEP (默认)
    {
        array<double> alpha;  alpha.push_back( -4.0 );  // -4 到 +10, 7 点
        SetDoubleAnalysisInput( "VSPAEROSweep", "AlphaStart", alpha );
        array<int> alpha_npts;  alpha_npts.push_back( 7 );
        SetIntAnalysisInput( "VSPAEROSweep", "AlphaNpts", alpha_npts );

        array<double> beta;  beta.push_back( 0.0 );
        SetDoubleAnalysisInput( "VSPAEROSweep", "BetaStart", beta );
        array<int> beta_npts;  beta_npts.push_back( 1 );
        SetIntAnalysisInput( "VSPAEROSweep", "BetaNpts", beta_npts );

        array<int> stab;  stab.push_back( STAB_OFF );
        SetIntAnalysisInput( "VSPAEROSweep", "UnsteadyType", stab );
    }

    Update();

    // ---- 生成网格数据库 (VSPAEROSweep 不自行建网格, 必须先跑这个) ----
    SetAnalysisInputDefaults( "VSPAEROComputeGeometry" );
    string gid = ExecAnalysis( "VSPAEROComputeGeometry" );
    Print( "geometry id: " + gid + "\n" );

    // ---- 运行求解 ----
    string rid = ExecAnalysis( "VSPAEROSweep" );
    Print( "sweep results id: " + rid + "\n" );
    Print( "=== Step 2 DONE (" + mode + ") ===\n" );
}
