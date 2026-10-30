using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using UnityEngine;

namespace FlightSim
{
    /// <summary>
    /// 机型工坊 W4: 从旧 VspImportUI 迁出的公共能力 (提示词 §11 W4: 旧 F8 面板能力
    /// 迁入新工坊并移除重复代码)。
    ///   - WorkshopFileDialog: comdlg32 打开/保存对话框 (P/Invoke, 不引 System.Windows.Forms)
    ///   - WorkshopLibrary:    机型库扫描 (VspImports/*/AircraftConfig.json)、report 读取、
    ///                          内部名 sanitize (与 Tools/import_vsp.py 一致)、overrides.json
    ///                          生成 (静态化, 原 WriteOverrides, 供管线 --overrides)
    /// </summary>
    internal static class WorkshopFileDialog
    {
        [System.Runtime.InteropServices.StructLayout(
            System.Runtime.InteropServices.LayoutKind.Sequential,
            CharSet = System.Runtime.InteropServices.CharSet.Unicode)]
        private struct OpenFileName
        {
            public int structSize;
            public System.IntPtr hwnd;
            public System.IntPtr hInstance;
            public string filter;
            public string customFilter;
            public int maxCustFilter;
            public int filterIndex;
            public string file;
            public int maxFile;
            public string fileTitle;
            public int maxFileTitle;
            public string initialDir;
            public string title;
            public int flags;
            public short fileOffset;
            public short fileExtension;
            public string defExt;
            public System.IntPtr custData;
            public System.IntPtr hook;
            public string templateName;
            public System.IntPtr reservedPtr;
            public int reservedInt;
            public int flagsEx;
        }

        [System.Runtime.InteropServices.DllImport("comdlg32.dll",
            CharSet = System.Runtime.InteropServices.CharSet.Unicode)]
        private static extern bool GetOpenFileNameW(ref OpenFileName ofn);

        [System.Runtime.InteropServices.DllImport("comdlg32.dll",
            CharSet = System.Runtime.InteropServices.CharSet.Unicode)]
        private static extern bool GetSaveFileNameW(ref OpenFileName ofn);

        private const int OFN_EXPLORER = 0x00080000;
        private const int OFN_FILEMUSTEXIST = 0x00001000;
        private const int OFN_NOCHANGEDIR = 0x00000008;
        private const int OFN_OVERWRITEPROMPT = 0x00000002;

        public static string PickOpenVsp3()
        {
            var ofn = new OpenFileName();
            ofn.structSize = System.Runtime.InteropServices.Marshal.SizeOf(ofn);
            ofn.filter = "OpenVSP 模型 (*.vsp3)\0*.vsp3\0所有文件 (*.*)\0*.*\0";
            ofn.file = new string(new char[512]);
            ofn.maxFile = ofn.file.Length;
            ofn.fileTitle = new string(new char[64]);
            ofn.maxFileTitle = ofn.fileTitle.Length;
            ofn.title = "选择 OpenVSP 模型 (.vsp3)";
            ofn.flags = OFN_EXPLORER | OFN_FILEMUSTEXIST | OFN_NOCHANGEDIR;
            try { return GetOpenFileNameW(ref ofn) ? ofn.file : null; }
            catch (System.Exception ex)
            {
                Debug.LogWarning("[WorkshopFileDialog] 打开对话框失败: " + ex.Message);
                return null;
            }
        }

        public static string PickSaveVsp3(string suggestedName)
        {
            var ofn = new OpenFileName();
            ofn.structSize = System.Runtime.InteropServices.Marshal.SizeOf(ofn);
            ofn.filter = "OpenVSP 模型 (*.vsp3)\0*.vsp3\0所有文件 (*.*)\0*.*\0";
            ofn.file = suggestedName ?? "";
            ofn.maxFile = 512;
            ofn.fileTitle = new string(new char[64]);
            ofn.maxFileTitle = ofn.fileTitle.Length;
            ofn.title = "保存 OpenVSP 模型 (.vsp3)";
            ofn.flags = OFN_EXPLORER | OFN_NOCHANGEDIR | OFN_OVERWRITEPROMPT;
            try { return GetSaveFileNameW(ref ofn) ? ofn.file : null; }
            catch (System.Exception ex)
            {
                Debug.LogWarning("[WorkshopFileDialog] 保存对话框失败: " + ex.Message);
                return null;
            }
        }

        /// <summary>打开 .dat/.af 翼型坐标文件。</summary>
        public static string PickOpenDat()
        {
            var ofn = new OpenFileName();
            ofn.structSize = System.Runtime.InteropServices.Marshal.SizeOf(ofn);
            ofn.filter = "翼型坐标 (*.dat;*.af;*.txt)\0*.dat;*.af;*.txt\0所有文件 (*.*)\0*.*\0";
            ofn.file = new string(new char[512]);
            ofn.maxFile = ofn.file.Length;
            ofn.fileTitle = new string(new char[64]);
            ofn.maxFileTitle = ofn.fileTitle.Length;
            ofn.title = "选择翼型坐标文件";
            ofn.flags = OFN_EXPLORER | OFN_FILEMUSTEXIST | OFN_NOCHANGEDIR;
            try { return GetOpenFileNameW(ref ofn) ? ofn.file : null; }
            catch (System.Exception ex)
            {
                Debug.LogWarning("[WorkshopFileDialog] 打开对话框失败: " + ex.Message);
                return null;
            }
        }
    }

    /// <summary>机型库与导入管线辅助 (原 VspImportUI 静态能力迁出, W4)。
    /// public: PlayMode 测试程序集直接断言 (与旧 VspImportUI.ScanLibrary 同可见性)。</summary>
    public static class WorkshopLibrary
    {
        public struct LibraryEntry
        {
            public string Dir;
            public string DisplayName;
            public bool HasObj;
            public bool HasReport;
            public float MassKg;
            public float SrefM2;
            public float BrefM;
            public int WarningCount;
            public System.DateTime ConfigMTimeUtc;
        }

        [Serializable]
        public class ReportJson
        {
            public string name;
            public float mass_kg;
            public float Sref_m2;
            public float bref_m;
            public float cref_m;
            public string[] warnings;
            public string[] skipped_controls;
        }

        /// <summary>读产物目录的 report.json; 不存在/解析失败返回 null。</summary>
        public static ReportJson ReadReportOrNull(string dir)
        {
            string path = Path.Combine(dir, "report.json");
            if (!File.Exists(path)) return null;
            try { return JsonUtility.FromJson<ReportJson>(File.ReadAllText(path)); }
            catch (System.Exception ex)
            {
                Debug.LogWarning("[WorkshopLibrary] report.json 解析失败 (" + dir + "): " + ex.Message);
                return null;
            }
        }

        /// <summary>扫描含 AircraftConfig.json 的产物目录, 按 config 修改时间倒序。</summary>
        public static List<LibraryEntry> ScanLibrary(string vspImportsRoot)
        {
            var list = new List<LibraryEntry>();
            if (!Directory.Exists(vspImportsRoot)) return list;
            foreach (string dir in Directory.GetDirectories(vspImportsRoot))
            {
                string cfg = Path.Combine(dir, "AircraftConfig.json");
                if (!File.Exists(cfg)) continue;
                var e = new LibraryEntry
                {
                    Dir = dir,
                    DisplayName = Path.GetFileName(dir),
                    HasObj = File.Exists(Path.Combine(dir, "model.obj")),
                    ConfigMTimeUtc = File.GetLastWriteTimeUtc(cfg),
                };
                var r = ReadReportOrNull(dir);
                if (r != null)
                {
                    e.HasReport = true;
                    if (!string.IsNullOrEmpty(r.name)) e.DisplayName = r.name;
                    e.MassKg = r.mass_kg;
                    e.SrefM2 = r.Sref_m2;
                    e.BrefM = r.bref_m;
                    e.WarningCount = (r.warnings != null ? r.warnings.Length : 0) +
                                     (r.skipped_controls != null ? r.skipped_controls.Length : 0);
                }
                list.Add(e);
            }
            list.Sort((a, b) => b.ConfigMTimeUtc.CompareTo(a.ConfigMTimeUtc));
            return list;
        }

        /// <summary>与 Tools/import_vsp.py 的 sanitize_internal 一致 (含 md5 前 6 位兜底)。</summary>
        public static string SanitizeInternal(string name)
        {
            var sb = new StringBuilder();
            foreach (char ch in name)
                if ((ch >= 'a' && ch <= 'z') || (ch >= 'A' && ch <= 'Z') ||
                    (ch >= '0' && ch <= '9') || ch == '_' || ch == '-')
                    sb.Append(ch);
            if (sb.Length > 0) return sb.ToString();
            using (var md5 = System.Security.Cryptography.MD5.Create())
            {
                byte[] h = md5.ComputeHash(Encoding.UTF8.GetBytes(name));
                return "uav_" + System.BitConverter.ToString(h, 0, 3).Replace("-", "").ToLower();
            }
        }

        // ================= overrides.json 生成 (原 WriteOverrides 静态化) =================

        /// <summary>部件级覆盖编辑项 (管线 --overrides 的 UI 数据; 新工坊槽位体系外的
        /// 高级定制入口, 原 VspImportUI 部件表)。</summary>
        public class CompEdit
        {
            public string Id;
            public string Name;
            public float MassDefault;
            public string Mass = "";    // 空=默认
            public int Role;            // 0自动 1主翼 2平尾 3垂尾 4机身 5忽略
            public int Ctl;             // 0自动 1无 2副翼 3升降舵 4方向舵
            public string Inc = "0";
        }

        public static readonly string[] RoleNames = { "自动", "主翼", "平尾", "垂尾", "机身", "忽略" };
        public static readonly string[] RoleValues = { "auto", "wing", "htail", "vtail", "fuselage", "fuselage" };
        public static readonly string[] CtlNames = { "自动", "无", "副翼", "升降舵", "方向舵" };
        public static readonly string[] CtlValues = { "auto", "none", "Aileron", "Elevator", "Rudder" };

        /// <summary>部件+全机编辑 → overrides.json 内容; 无任何修改返回 null。
        /// outDir 传入时写盘并返回路径, 否则只返回 JSON 文本 (测试用)。</summary>
        public static string WriteOverrides(List<CompEdit> comps,
            float ballastKg, float ballastX, bool gearAuto, string outDir = null)
        {
            var sb = new StringBuilder();
            sb.Append("{\n");
            bool any = false;

            if (ballastKg > 0.01f)
            {
                sb.Append("\"ballast_kg\": ").Append(ballastKg.ToString("R")).Append(",\n");
                sb.Append("\"ballast_pos_m\": [").Append(ballastX.ToString("R")).Append(", 0, 0],\n");
                any = true;
            }
            if (gearAuto)
            {
                sb.Append("\"gear\": {\"auto\": true},\n");
                any = true;
            }

            var compSb = new StringBuilder();
            int n = 0;
            foreach (var c in comps ?? new List<CompEdit>())
            {
                bool massSet = float.TryParse(c.Mass, out float mkg) && mkg > 0.01f &&
                               Mathf.Abs(mkg - c.MassDefault) > 0.005f;
                bool changed = c.Role != 0 || c.Ctl != 0 || massSet ||
                               (float.TryParse(c.Inc, out float inc) && Mathf.Abs(inc) > 1e-4f);
                if (!changed) continue;
                bool ignore = c.Role == 5;
                string selector = string.IsNullOrEmpty(c.Id) ? c.Name : "geom:" + c.Id;
                compSb.Append("    \"").Append(selector.Replace("\\", "\\\\").Replace("\"", "\\\""))
                      .Append("\": {");
                compSb.Append("\"role\": \"").Append(RoleValues[c.Role]).Append("\"");
                if (ignore) compSb.Append(", \"ignore\": true");
                if (c.Ctl != 0) compSb.Append(", \"control\": \"").Append(CtlValues[c.Ctl]).Append("\"");
                if (massSet)
                    compSb.Append(", \"weight_kg\": ").Append(mkg.ToString("R",
                        System.Globalization.CultureInfo.InvariantCulture));
                if (float.TryParse(c.Inc, out float inc2) && Mathf.Abs(inc2) > 1e-4f)
                    compSb.Append(", \"incidence_deg\": ").Append(inc2.ToString("R",
                        System.Globalization.CultureInfo.InvariantCulture));
                compSb.Append("},\n");
                n++;
            }
            if (n > 0)
            {
                sb.Append("\"components\": {\n").Append(compSb.ToString().TrimEnd(',', '\n'))
                  .Append("\n  },\n");
                any = true;
            }
            if (!any) return null;

            sb.Append("  \"_note\": \"generated by WorkshopLibrary\"\n}");
            string json = sb.ToString();
            if (outDir == null) return json;
            try
            {
                Directory.CreateDirectory(outDir);
                string p = Path.Combine(outDir, "overrides.json");
                File.WriteAllText(p, json);
                return p;
            }
            catch (System.Exception)
            {
                return null;
            }
        }
    }
}
