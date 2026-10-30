// 默认机 TestUAV 分部件 STL 导出 (R4 分组 OBJ 用, 复刻 import_vsp prep 的导出逻辑)
// 由 vspscript -script 调用, CWD = Data/:
//   打开 TestUAV.vsp3 → 清除 MESH 缓存几何 → 逐几何导出 work/testuav_parts/part_i.stl
// (发布版 3.51.3 API 无 DeleteMeshGeoms, 手工按类型删)
void DeleteBakedMeshes()
{
    array<string> gs = FindGeoms();
    for ( uint i = 0; i < gs.size(); i++ )
        if ( GetGeomTypeName( gs[i] ) == "Mesh" )
            DeleteGeom( gs[i] );
}

void main()
{
    ReadVSPFile( "TestUAV.vsp3" );

    // ExportFile(STL) 会直接导出集合里已有的 MESH 而不重新烘 — 先删干净
    DeleteBakedMeshes();

    array<string> ids = FindGeoms();
    for ( uint i = 0; i < ids.size(); i++ )
        SetSetFlag( ids[i], 3, true );

    // ExportFile(STL) 烘出的 MESH 会留在场景并被下次导出复用 — 每轮必须删掉
    DeleteBakedMeshes();
    for ( uint i = 0; i < ids.size(); i++ )
    {
        for ( uint j = 0; j < ids.size(); j++ )
            SetSetFlag( ids[j], 3, j == i );
        Update();
        ExportFile( "../work/testuav_parts/part_" + i + ".stl", 3, EXPORT_STL );
        Print( "[PART] " + i + " " + GetGeomName( ids[i] ) + "\n" );
        DeleteBakedMeshes();
    }
    for ( uint j = 0; j < ids.size(); j++ )
        SetSetFlag( ids[j], 3, true );
    Print( "[PARTS_DONE] " + ids.size() + "\n" );
}
