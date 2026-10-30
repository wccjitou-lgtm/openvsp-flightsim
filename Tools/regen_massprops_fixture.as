void main()
{
    string pod = AddGeom( "POD", "" );
    SetGeomName( pod, "PodGeom" );
    string wing = AddGeom( "WING", "" );
    SetGeomName( wing, "WingGeom" );
    Update();
    SetAnalysisInputDefaults( "MassProp" );
    string r = ExecAnalysis( "MassProp" );
    Print( "done" );
}
