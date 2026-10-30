using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Threading;
using UnityEngine;

namespace FlightSim
{
    /// <summary>
    /// 机型工坊 W3: 栏② 气动计算面板 (严格复刻参考图分组/标签/默认值, 提示词 §6)
    /// + 运行区 (运行计算/停止/进度/实时日志/结果图表/CSV/稳定性摘要)。
    /// 字段 → VSPAERO 输入的映射在 Tools/workshop_aero.py (运行时反查表见
    /// docs/workshop_param_map.md §6); 运行 = 独立 vspscript 进程 (铁律 1/2)。
    /// </summary>
    public class WorkshopAeroPanel
    {
        // ---------- 状态 (全部字段; 空工程默认=参考图) ----------
        public string thinSet = "shown", thickSet = "none";
        public bool symmetry;
        public bool cullFlag = true;
        public float cullFrac = 0.030f;
        public int ncpu = 4;
        public bool fem2d, tecplot;
        public bool fixedWake;
        public int wakeIter = 3, wakeNodes = 8, freezeIter = 10000, implicitStart = 0;
        public float wakeRelax = 1.0f;
        public Vector3 alphaRange = new Vector3(0f, 10f, 3f);
        public Vector3 betaRange = new Vector3(0f, 0f, 1f);
        public Vector3 machRange = new Vector3(0f, 0f, 1f);
        public Vector3 recrefRange = new Vector3(1e7f, 2e7f, 1f);
        public bool refFromModel;                  // 机翼1激活后自动切 true (唯一偏离参考图)
        public string refChoice = "stot";          // cave/mac/stot/scurve
        public float sref = 100f, bref = 1f, cref = 1f;
        public float xref = 10.105f, yref, zref;
        public bool refManual;                     // false=力矩参考位置跟随重心 (默认)
        public bool autoRefApplied;
        public readonly List<CtlDeflection> Controls = new List<CtlDeflection>();

        public class CtlDeflection
        {
            public string name;
            public float deg;
            public bool mirrored;                  // Elevator=镜像对称, Aileron=反对称
        }

        // 运行状态
        public bool Running { get; private set; }
        public string ResultJson = "";             // 最近一次 aero_result.json 内容
        public string ResultDir = "";
        public readonly List<string> Log = new List<string>();
        private Process _proc;
        private readonly ConcurrentQueue<string> _lines = new ConcurrentQueue<string>();

        private readonly WorkshopUI _owner;
        private Vector2 _scroll;

        public WorkshopAeroPanel(WorkshopUI owner) { _owner = owner; }

        // ================= 绘制 =================
        public void Draw(WorkshopState state)
        {
            _scroll = GUILayout.BeginScrollView(_scroll, false, true);

            WorkshopUiKit.SectionBar("算例设置");
            GUILayout.BeginHorizontal();
            GUILayout.Label("文件", GUILayout.Width(WorkshopUiKit.LabelW));
            // 默认用 ASCII 内部名 (vspaero 窄字符产物要求), 中文工程名显示在标题栏
            string caseFile = GUILayout.TextField(
                BufOf("caseFile", WorkshopLibrary.SanitizeInternal(state.projectName) + ".vspgeom"),
                GUILayout.ExpandWidth(true));
            _bufs["caseFile"] = caseFile;
            GUILayout.Label("ASCII", WorkshopUiKit.Dim, GUILayout.Width(40));
            GUILayout.EndHorizontal();
            SetRow(ref thinSet, "薄面集合", new[] { "shown", "none", "3" },
                   new[] { "Shown", "None", "用户集3" });
            SetRow(ref thickSet, "厚体集合", new[] { "none", "shown", "3" },
                   new[] { "None", "Shown", "用户集3" });
            GUILayout.BeginHorizontal();
            GUILayout.Label("模式", GUILayout.Width(WorkshopUiKit.LabelW));
            using (var s = new TempDisable(true))
                GUILayout.Button("无质量模式", GUILayout.Width(110));   // 质量模式 W4 (换机联动)
            GUILayout.Label("质量模式随换机", WorkshopUiKit.Dim);
            GUILayout.EndHorizontal();

            WorkshopUiKit.SectionBar("VSPAERO 几何");
            symmetry = GUILayout.Toggle(symmetry, "X-Z 对称");
            // V 轮修复: 剔片面片/CPU 原先把滑块、数值、单位垂直堆成三行, 现统一单行
            GUILayout.BeginHorizontal();
            cullFlag = GUILayout.Toggle(cullFlag, "剔除孤立面片", GUILayout.Width(112));
            using (var s = new TempDisable(!cullFlag))
            {
                float cf = GUILayout.HorizontalSlider(cullFrac, 0f, 0.2f,
                    GUILayout.ExpandWidth(true), GUILayout.MaxWidth(160));
                cullFrac = Mathf.Round(cf * 1000f) / 1000f;
                GUILayout.Label(cullFrac.ToString("0.000"), WorkshopUiKit.Dim,
                                GUILayout.Width(48));
            }
            GUILayout.EndHorizontal();
            GUILayout.BeginHorizontal();
            GUILayout.Label("CPU 数量", GUILayout.Width(112));
            int nSl = Mathf.RoundToInt(GUILayout.HorizontalSlider(ncpu, 1,
                SystemInfo.processorCount, GUILayout.ExpandWidth(true), GUILayout.MaxWidth(160)));
            if (nSl != ncpu) { ncpu = nSl; _bufs.Remove("ncpu"); }
            float fN = ncpu;
            NumField("ncpu", ref fN, "0", 46f);
            ncpu = Mathf.Clamp(Mathf.RoundToInt(fN), 1, SystemInfo.processorCount);
            GUILayout.Label("核", WorkshopUiKit.Dim, GUILayout.Width(WorkshopUiKit.UnitW));
            GUILayout.EndHorizontal();
            GUILayout.BeginHorizontal();
            fem2d = GUILayout.Toggle(fem2d, "输出二维 FEM", GUILayout.Width(130));
            tecplot = GUILayout.Toggle(tecplot, "输出 Tecplot 文件");
            GUILayout.EndHorizontal();

            WorkshopUiKit.SectionBar("尾迹");
            fixedWake = GUILayout.Toggle(fixedWake, "固定尾迹");
            IntField("迭代次数", ref wakeIter, 1, 100);
            IntField("节点数", ref wakeNodes, 1, 64);
            FloatLabel("松弛因子", ref wakeRelax, "0.###");
            IntField("冻结迭代步", ref freezeIter, 1, 1000000);
            IntField("隐式起始步", ref implicitStart, 0, 1000000);

            WorkshopUiKit.SectionBar("操纵面组偏角");
            if (Controls.Count == 0)
                GUILayout.Label("尚未创建操纵面 (在机翼/尾翼编辑窗中新建)", WorkshopUiKit.Dim);
            foreach (var c in Controls)
            {
                GUILayout.BeginHorizontal();
                GUILayout.Label(c.name + (c.mirrored ? " (对称)" : " (反对称)"),
                                GUILayout.Width(110));
                float deg = GUILayout.HorizontalSlider(c.deg, -30f, 30f,
                    GUILayout.ExpandWidth(true));
                c.deg = Mathf.Round(deg * 10f) / 10f;
                GUILayout.Label(c.deg.ToString("0.0") + "°", WorkshopUiKit.Dim,
                                GUILayout.Width(48));
                GUILayout.EndHorizontal();
            }

            WorkshopUiKit.SectionBar("来流条件");
            RangeHeader();
            RangeRow("迎角", ref alphaRange, "0.#");
            RangeRow("侧滑角", ref betaRange, "0.#");
            RangeRow("马赫数", ref machRange, "0.###");
            RangeRow("ReCref", ref recrefRange, "0.#e+00");

            WorkshopUiKit.SectionBar("参考面积与长度");
            GUILayout.BeginHorizontal();
            refFromModel = GUILayout.Toggle(!refFromModel, "手动", GUILayout.Width(76)) ? false : refFromModel;
            refFromModel = GUILayout.Toggle(refFromModel, "取自模型", GUILayout.Width(96)) ? true : refFromModel;
            using (var s = new TempDisable(!refFromModel))
            {
                GUILayout.Label("参考机翼", WorkshopUiKit.Dim, GUILayout.Width(56));
                if (GUILayout.Button(RefZh(refChoice) + " ▾", GUILayout.Width(90)))
                    refChoice = NextRef(refChoice);
            }
            GUILayout.EndHorizontal();
            using (var s = new TempDisable(refFromModel))
            {
                FloatLabel("Sref", ref sref, "0.###");
                FloatLabel("bref", ref bref, "0.###");
                FloatLabel("cref", ref cref, "0.###");
            }

            WorkshopUiKit.SectionBar("力矩参考位置");
            bool follow = GUILayout.Toggle(!refManual, "跟随重心 (默认)");
            if (follow == refManual)
            {
                refManual = !follow;
                if (follow) _owner.RequestComputeCg(this);
            }
            using (var s = new TempDisable(!refManual))
            {
                FloatLabel("Xref", ref xref, "0.###");
                FloatLabel("Yref", ref yref, "0.###");
                FloatLabel("Zref", ref zref, "0.###");
            }
            if (GUILayout.Button("计算重心 (MassProp → 写回参考位置)",
                                 GUILayout.Height(WorkshopUiKit.BtnH)))
                _owner.RequestComputeCg(this);
            GUILayout.Label("切片方向 X · 切片数 10 (MassProp 输入口径)", WorkshopUiKit.Dim);

            WorkshopUiKit.SectionBar("运行");
            GUILayout.BeginHorizontal();
            using (var s = new TempDisable(Running || _owner.SwapBusy))   // U7: 换机中禁跑 (CPU 争用)
                if (GUILayout.Button("运行计算", GUILayout.Width(100),
                                     GUILayout.Height(WorkshopUiKit.BtnH)))
                    _owner.StartAeroRun(this);
            using (var s = new TempDisable(!Running))
                if (GUILayout.Button("停止", GUILayout.Width(70),
                                     GUILayout.Height(WorkshopUiKit.BtnH)))
                    Cancel();
            GUILayout.Label(Running ? "计算中…" : (ResultJson.Length > 0 ? "已完成" : "未运行"),
                            WorkshopUiKit.Dim);
            GUILayout.EndHorizontal();
            DrawStability();
            if (ResultJson.Length > 0) DrawChart();

            GUILayout.EndScrollView();
        }

        private void DrawStability()
        {
            if (ResultJson.Length == 0) return;
            float dCmdCL = WorkshopJson.Num(ResultJson, "dCm_dCL", float.NaN);
            float xnp = WorkshopJson.Num(ResultJson, "Xnp_m", float.NaN);
            float cr = WorkshopJson.Num(ResultJson, "cref", 1f);
            float xcg = WorkshopJson.Num(ResultJson, "xref_m", 0f);
            if (float.IsNaN(dCmdCL)) return;
            float sm = (xnp - xcg) / cr * 100f;
            GUILayout.Label(string.Format(
                "dCm/dCL = {0:+0.000;-0.000}   Xnp = {1:0.000} m (VSPAERO)   SM = {2:+0.0;-0.0}%",
                dCmdCL, xnp, sm));
            if (sm < 0f) GUILayout.Label("⚠ 静不稳定 (SM < 0)", RedLabel());
            else if (sm > 40f) GUILayout.Label("⚠ 过稳定 (SM > 40%)", YellowLabel());
        }

        // ---------- 图表 (IMGUI 折线, Texture2D 绘制) ----------
        private Texture2D _chartTex;

        private void DrawChart()
        {
            var xs = FloatArr(ResultJson, "alpha");
            var ys = FloatArr(ResultJson, "CL");
            var y2 = FloatArr(ResultJson, "CD");
            if (xs == null || xs.Length < 2) return;
            if (_chartTex == null)
            {
                _chartTex = new Texture2D(320, 150, TextureFormat.RGBA32, false);
            }
            DrawSeries(_chartTex, xs, ys, new Color(0.4f, 0.9f, 0.5f));
            GUILayout.Label("CL–α (绿) / CD–α (蓝, ×10)");
            GUILayout.Box(_chartTex, GUILayout.Width(320), GUILayout.Height(150));
            DrawSeries(_chartTex, xs, y2, new Color(0.4f, 0.7f, 1f), 10f);
            if (GUILayout.Button("导出 CSV", GUILayout.Width(90)))
                ExportCsv();
        }

        private static float[] FloatArr(string json, string field)
        {
            string arr = WorkshopJson.Raw(json, field);
            if (arr == null) return null;
            var toks = arr.Trim('[', ']').Split(',');
            var res = new float[toks.Length];
            for (int i = 0; i < toks.Length; i++)
                float.TryParse(toks[i].Trim(), System.Globalization.NumberStyles.Float,
                    System.Globalization.CultureInfo.InvariantCulture, out res[i]);
            return res;
        }

        private static void DrawSeries(Texture2D tex, float[] xs, float[] ys, Color col,
                                       float yScale = 1f)
        {
            int w = tex.width, h = tex.height;
            var bg = new Color32(16, 24, 36, 255);
            for (int y = 0; y < h; y++)
                for (int x = 0; x < w; x++)
                    tex.SetPixel(x, y, bg);
            float xmin = Mathf.Min(xs), xmax = Mathf.Max(xs);
            float ymin = Mathf.Min(ys) * yScale, ymax = Mathf.Max(ys) * yScale;
            if (Mathf.Approximately(xmax, xmin)) xmax = xmin + 1f;
            if (Mathf.Approximately(ymax, ymin)) ymax = ymin + 1f;
            for (int i = 1; i < xs.Length; i++)
            {
                int x0 = Mathf.RoundToInt((xs[i - 1] - xmin) / (xmax - xmin) * (w - 4)) + 2;
                int x1 = Mathf.RoundToInt((xs[i] - xmin) / (xmax - xmin) * (w - 4)) + 2;
                int y0 = Mathf.RoundToInt((ys[i - 1] * yScale - ymin) / (ymax - ymin) * (h - 8)) + 4;
                int y1 = Mathf.RoundToInt((ys[i] * yScale - ymin) / (ymax - ymin) * (h - 8)) + 4;
                int steps = Mathf.Max(Mathf.Abs(x1 - x0), Mathf.Abs(y1 - y0)) + 1;
                for (int s = 0; s <= steps; s++)
                {
                    int px = Mathf.RoundToInt(Mathf.Lerp(x0, x1, s / (float)steps));
                    int py = Mathf.RoundToInt(Mathf.Lerp(y0, y1, s / (float)steps));
                    if (px >= 0 && px < w && py >= 0 && py < h) tex.SetPixel(px, py, col);
                }
            }
            tex.Apply();
        }

        private void ExportCsv()
        {
            var xs = FloatArr(ResultJson, "alpha");
            var cl = FloatArr(ResultJson, "CL");
            var cd = FloatArr(ResultJson, "CD");
            var cm = FloatArr(ResultJson, "Cm");
            var ld = FloatArr(ResultJson, "LD");
            if (xs == null || cl == null) return;
            string dir = Path.Combine(ResultDir, "aero_export.csv");
            var sb = new StringBuilder("alpha_deg,CL,CD,Cm,L/D\n");
            for (int i = 0; i < xs.Length; i++)
                sb.Append(xs[i].ToString("0.####",
                    System.Globalization.CultureInfo.InvariantCulture)).Append(',')
                  .Append(cl[i]).Append(',').Append(cd != null ? cd[i].ToString() : "")
                  .Append(',').Append(cm != null ? cm[i].ToString() : "")
                  .Append(',').Append(ld != null ? ld[i].ToString("0.000") : "").Append('\n');
            File.WriteAllText(dir, sb.ToString());
            Log.Add("[csv] " + dir);
        }

        // ================= 运行管线 (轻量 PipelineRunner: 行缓冲 + taskkill) =================
        public int RunModelVersion { get; private set; } = -1;

        public bool Start(string pythonExe, string projectRoot, string vspPath)
        {
            if (Running) return false;
            RunModelVersion = _owner.ModelVersion;   // R10: 结果绑定发起时的模型版本
            // 面板参数 → JSON (workshop_aero._DEFAULTS 键名)
            var sb = new StringBuilder("{");
            sb.Append("\"thin_set\":\"").Append(thinSet).Append("\",\"thick_set\":\"").Append(thickSet)
              .Append("\",\"symmetry\":").Append(symmetry ? "true" : "false")
              .Append(",\"ncpu\":").Append(ncpu)
              .Append(",\"fem2d\":").Append(fem2d ? "true" : "false")
              .Append(",\"tecplot\":").Append(tecplot ? "true" : "false")
              .Append(",\"wake\":{\"fixed\":").Append(fixedWake ? "true" : "false")
              .Append(",\"iter\":").Append(wakeIter).Append(",\"nodes\":").Append(wakeNodes)
              .Append(",\"relax\":").Append(wakeRelax.ToString("0.###",
                  System.Globalization.CultureInfo.InvariantCulture))
              .Append(",\"freeze\":").Append(freezeIter).Append(",\"implicit_start\":").Append(implicitStart)
              .Append("},\"cull\":{\"flag\":").Append(cullFlag ? "true" : "false")
              .Append(",\"frac\":").Append(cullFrac.ToString("0.000",
                  System.Globalization.CultureInfo.InvariantCulture)).Append('}')
              .Append(",\"alpha\":[").Append(RangeStr(alphaRange)).Append(']')
              .Append(",\"beta\":[").Append(RangeStr(betaRange)).Append(']')
              .Append(",\"mach\":[").Append(RangeStr(machRange)).Append(']')
              .Append(",\"recref\":[").Append(RangeStr(recrefRange)).Append(']')
              .Append(",\"ref_mode\":\"").Append(refFromModel ? "model" : "manual").Append('"')
              .Append(",\"ref_choice\":\"").Append(refChoice).Append('"')
              .Append(",\"ref_wing_id\":").Append(WorkshopJson.Escape(_owner.Wing1GeomId))
              .Append(",\"sref\":").Append(sref).Append(",\"bref\":").Append(bref)
              .Append(",\"cref\":").Append(cref)
              .Append(",\"xref\":").Append(xref).Append(",\"yref\":").Append(yref)
              .Append(",\"zref\":").Append(zref)
              .Append(",\"controls\":[");
            for (int i = 0; i < Controls.Count; i++)
            {
                if (i > 0) sb.Append(',');
                sb.Append("{\"name\":").Append(WorkshopJson.Escape(Controls[i].name))
                  .Append(",\"deflection_deg\":").Append(Controls[i].deg.ToString("0.#",
                      System.Globalization.CultureInfo.InvariantCulture)).Append('}');
            }
            sb.Append("]}");
            string work = Path.Combine(projectRoot, "work", "workshop_aero_ui").Replace('\\', '/');
            string panelFile = Path.Combine(work, "panel.json").Replace('\\', '/');
            Directory.CreateDirectory(work);
            File.WriteAllText(panelFile, sb.ToString());

            string args = $"\"{projectRoot.Replace('\\', '/')}/Tools/workshop_aero.py\" " +
                          $"--vsp \"{vspPath.Replace('\\', '/')}\" --panel \"{panelFile}\" " +
                          $"--work \"{work}\"";
            var psi = new ProcessStartInfo
            {
                FileName = pythonExe,
                Arguments = args,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
                StandardOutputEncoding = System.Text.Encoding.UTF8,
                StandardErrorEncoding = System.Text.Encoding.UTF8,
                WorkingDirectory = projectRoot,
            };
            psi.EnvironmentVariables["PYTHONIOENCODING"] = "utf-8";
            Log.Clear();
            Running = true;
            ResultJson = "";
            _proc = new Process { StartInfo = psi, EnableRaisingEvents = true };
            _proc.OutputDataReceived += (_, e) => { if (e.Data != null) _lines.Enqueue(e.Data); };
            _proc.ErrorDataReceived += (_, e) => { if (e.Data != null) _lines.Enqueue("[py] " + e.Data); };
            try
            {
                _proc.Start();
                _proc.BeginOutputReadLine();
                _proc.BeginErrorReadLine();
                new Thread(() =>
                {
                    _proc.WaitForExit();
                    Running = false;
                    // 成败看日志里 [AERO_STAGE] 标志 (装机版 vspscript 成功也退出码 1,
                    // 显示退出码反而误导 — U7)
                    _lines.Enqueue("[ui] 结束");
                }) { IsBackground = true }.Start();
            }
            catch (System.Exception ex)
            {
                Running = false;
                Log.Add("[错误] " + ex.Message);
                return false;
            }
            return true;
        }

        private static string RangeStr(Vector3 r)
        {
            return r.x.ToString("0.######",
                System.Globalization.CultureInfo.InvariantCulture) + "," +
                r.y.ToString("0.######",
                System.Globalization.CultureInfo.InvariantCulture) + "," +
                Mathf.RoundToInt(r.z);
        }

        /// <summary>主线程每帧调用: 行缓冲 → 日志; [done] 行读取结果 JSON。</summary>
        public void Poll(WorkshopState state)
        {
            while (_lines.TryDequeue(out string line))
            {
                Log.Add(line);
                if (Log.Count > 400) Log.RemoveAt(0);
                if (line.StartsWith("[done] "))
                {
                    // R10: 模型已变化的结果丢弃 (旧 NP 与当前 CG/参考混算会误判稳定性)
                    if (RunModelVersion >= 0 && _owner.ModelVersion != RunModelVersion)
                    {
                        Log.Add("[气动] 结果已丢弃 (模型在计算期间发生变化)");
                        RunModelVersion = -1;
                        return;
                    }
                    string jsonPath = line.Substring(7).Split(' ')[0];
                    if (File.Exists(jsonPath))
                    {
                        ResultJson = File.ReadAllText(jsonPath);
                        ResultDir = Path.GetDirectoryName(jsonPath);
                    }
                }
            }
        }

        public void Cancel()
        {
            var p = _proc;
            if (p != null && !p.HasExited)
                try { Process.Start("taskkill", $"/PID {p.Id} /T /F"); } catch { }
        }

        // ---------- 持久化 (workshop.json 的 aeroJson 字段, 提示词 §9 "aero") ----------
        [System.Serializable]
        private class AeroSave
        {
            public string thinSet, thickSet, refChoice;
            public bool symmetry, cullFlag, fem2d, tecplot, fixedWake, refFromModel, refManual;
            public float cullFrac, wakeRelax, sref, bref, cref, xref, yref, zref;
            public int ncpu, wakeIter, wakeNodes, freezeIter, implicitStart;
            public Vector3 alphaRange, betaRange, machRange, recrefRange;
        }

        public string ToJson()
        {
            var s = new AeroSave
            {
                thinSet = thinSet, thickSet = thickSet, refChoice = refChoice,
                symmetry = symmetry, cullFlag = cullFlag, fem2d = fem2d, tecplot = tecplot,
                fixedWake = fixedWake, refFromModel = refFromModel, refManual = refManual,
                cullFrac = cullFrac, wakeRelax = wakeRelax,
                sref = sref, bref = bref, cref = cref,
                xref = xref, yref = yref, zref = zref,
                ncpu = ncpu, wakeIter = wakeIter, wakeNodes = wakeNodes,
                freezeIter = freezeIter, implicitStart = implicitStart,
                alphaRange = alphaRange, betaRange = betaRange,
                machRange = machRange, recrefRange = recrefRange,
            };
            return JsonUtility.ToJson(s);
        }

        public void ApplyJson(string json)
        {
            var s = JsonUtility.FromJson<AeroSave>(json);
            if (s == null) return;
            if (!string.IsNullOrEmpty(s.thinSet)) thinSet = s.thinSet;
            if (!string.IsNullOrEmpty(s.thickSet)) thickSet = s.thickSet;
            if (!string.IsNullOrEmpty(s.refChoice)) refChoice = s.refChoice;
            symmetry = s.symmetry; cullFlag = s.cullFlag; fem2d = s.fem2d;
            tecplot = s.tecplot; fixedWake = s.fixedWake; refFromModel = s.refFromModel;
            refManual = s.refManual;
            cullFrac = s.cullFrac; wakeRelax = s.wakeRelax;
            sref = s.sref; bref = s.bref; cref = s.cref;
            xref = s.xref; yref = s.yref; zref = s.zref;
            ncpu = s.ncpu; wakeIter = s.wakeIter; wakeNodes = s.wakeNodes;
            freezeIter = s.freezeIter; implicitStart = s.implicitStart;
            if (s.alphaRange != default(Vector3)) alphaRange = s.alphaRange;
            if (s.betaRange != default(Vector3)) betaRange = s.betaRange;
            if (s.machRange != default(Vector3)) machRange = s.machRange;
            if (s.recrefRange != default(Vector3)) recrefRange = s.recrefRange;
        }

        // ================= 小控件 =================
        // V 轮: 分组标题条与弱化样式统一由 WorkshopUiKit 提供; 标签列宽统一 LabelW。
        private static void SetRow(ref string val, string label, string[] keys, string[] zh)
        {
            GUILayout.BeginHorizontal();
            GUILayout.Label(label, GUILayout.Width(WorkshopUiKit.LabelW));
            int cur = System.Array.IndexOf(keys, val);
            if (GUILayout.Button((cur >= 0 ? zh[cur] : val) + " ▾", GUILayout.Width(110),
                                 GUILayout.Height(WorkshopUiKit.RowH)))
                val = keys[((cur + 1) % keys.Length + keys.Length) % keys.Length];
            GUILayout.EndHorizontal();
        }

        // ---- 数值输入缓冲 (用户反馈修复: 删空被旧值覆盖 / 小数点被吃) ----
        // IMGUI 经典坑: 每帧用 v.ToString() 重建 TextField 会打断编辑。改为每框
        // 独立字符串缓冲: 编辑中显示缓冲, 解析成功才回写 v; 失焦时才规范化显示。
        private readonly Dictionary<string, string> _bufs = new Dictionary<string, string>();
        private string _focusKey;

        private string BufOf(string key, string def)
        {
            string s;
            return _bufs.TryGetValue(key, out s) ? s : def;
        }

        private void NumField(string key, ref float v, string fmt, float w)
        {
            GUI.SetNextControlName(key);
            string t = GUILayout.TextField(BufOf(key, v.ToString(fmt)), GUILayout.Width(w));
            _bufs[key] = t;
            bool focused = GUI.GetNameOfFocusedControl() == key;
            if (focused)
            {
                _focusKey = key;
                if (float.TryParse(t, System.Globalization.NumberStyles.Float,
                        System.Globalization.CultureInfo.InvariantCulture, out float f) &&
                    !Mathf.Approximately(f, v))
                    v = f;   // 编辑中也实时回写合法值 (滑块/计算联动)
            }
            else if (_focusKey == key)
            {
                _focusKey = null;
                _bufs[key] = v.ToString(fmt);   // 失焦: 规范化显示
            }
        }

        private void IntField(string label, ref int v, int min, int max)
        {
            GUILayout.BeginHorizontal();
            GUILayout.Label(label, GUILayout.Width(WorkshopUiKit.LabelW));
            int nv = Mathf.RoundToInt(GUILayout.HorizontalSlider(v, min, max,
                GUILayout.ExpandWidth(true), GUILayout.MaxWidth(200)));
            if (nv != v) { v = Mathf.Clamp(nv, min, max); _bufs.Remove(label + ".n"); }
            float fv = v;
            NumField(label + ".n", ref fv, "0", WorkshopUiKit.FieldW);
            v = Mathf.Clamp(Mathf.RoundToInt(fv), min, max);
            GUILayout.EndHorizontal();
        }

        private void FloatLabel(string label, ref float v, string fmt)
        {
            GUILayout.BeginHorizontal();
            GUILayout.Label(label, GUILayout.Width(WorkshopUiKit.LabelW));
            NumField(label, ref v, fmt, WorkshopUiKit.FieldW + 10f);
            GUILayout.EndHorizontal();
        }

        // ---- 来流条件区间行: 起值 → 终值 × 点数 (固定列宽与表头对齐; 旧版 w*0.3 把
        //      输入框压到 7px 是 "UI 完全混乱" 的根因, U05 改为显式像素宽) ----
        private const float RangeLabelW = 56f, RangeFieldW = 64f, RangeSepW = 12f,
                            RangeCountW = 40f;

        private static void RangeHeader()
        {
            GUILayout.BeginHorizontal();
            GUILayout.Label("", GUILayout.Width(RangeLabelW));
            GUILayout.Label("初值", WorkshopUiKit.TableHead, GUILayout.Width(RangeFieldW));
            GUILayout.Label("", GUILayout.Width(RangeSepW));
            GUILayout.Label("终值", WorkshopUiKit.TableHead, GUILayout.Width(RangeFieldW));
            GUILayout.Label("", GUILayout.Width(RangeSepW));
            GUILayout.Label("点数", WorkshopUiKit.TableHead, GUILayout.Width(RangeCountW));
            GUILayout.EndHorizontal();
        }

        private void RangeRow(string label, ref Vector3 r, string fmt)
        {
            GUILayout.BeginHorizontal();
            GUILayout.Label(label, GUILayout.Width(RangeLabelW));
            NumField(label + ".s", ref r.x, fmt, RangeFieldW);
            GUILayout.Label("→", WorkshopUiKit.Dim, GUILayout.Width(RangeSepW));
            using (var s = new TempDisable(r.z <= 1f))   // R11: 单点时终值置灰 (点数始终可编)
                NumField(label + ".e", ref r.y, fmt, RangeFieldW);
            GUILayout.Label("×", WorkshopUiKit.Dim, GUILayout.Width(RangeSepW));
            float n = r.z;
            NumField(label + ".n", ref n, "0", RangeCountW);
            r.z = Mathf.Clamp(Mathf.Round(n), 1f, 99f);
            GUILayout.EndHorizontal();
            GUILayout.Space(1);
        }

        private static string RefZh(string key)
        {
            switch (key)
            {
                case "cave": return "Cave";
                case "mac": return "MAC";
                case "scurve": return "Scurve";
                default: return "Stot";
            }
        }

        private static string NextRef(string key)
        {
            switch (key)
            {
                case "stot": return "cave";
                case "cave": return "mac";
                case "mac": return "scurve";
                default: return "stot";
            }
        }

        private static GUIStyle RedLabel()
        {
            var s = new GUIStyle(GUI.skin.label);
            s.normal.textColor = FluentTheme.Error;
            return s;
        }

        private static GUIStyle YellowLabel()
        {
            var s = new GUIStyle(GUI.skin.label);
            s.normal.textColor = FluentTheme.WarningOnLight;
            return s;
        }

        private struct TempDisable : System.IDisposable
        {
            private readonly bool _prev;
            public TempDisable(bool disabled) { _prev = GUI.enabled; if (disabled) GUI.enabled = false; }
            public void Dispose() { GUI.enabled = _prev; }
        }
    }
}
