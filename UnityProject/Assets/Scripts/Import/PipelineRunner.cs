using System.Collections.Concurrent;
using System.Diagnostics;
using System.Threading;
using UnityEngine;

namespace FlightSim
{
    /// <summary>
    /// 导入管线进程编排 (Phase 7c): 后台跑 python Tools/import_vsp.py,
    /// stdout/stderr 按行进 ConcurrentQueue, 主线程 Drain 回调 (OnGUI 只能主线程)。
    /// 进度解析依赖 import_vsp.py 的标记行: [stage] i/N label / [ok] label /
    /// [fail] ... / [done] 产物: 目录 (用时 Ns)。
    /// 取消用 taskkill /T /F 杀进程树 (python 的 vspscript 子进程不会被
    /// Process.Kill 连带杀掉)。
    /// </summary>
    public class PipelineRunner
    {
        public bool IsRunning { get; private set; }
        /// <summary>[done] 行解析出的产物目录 (空 = 尚未完成)。</summary>
        public string OutDir { get; private set; } = "";
        public string LastError { get; private set; } = "";
        /// <summary>队列里还有未取走的行 (进程已退出但 [done]/[ui] 结束 尚未 Drain 时为真)。</summary>
        public bool HasPending { get { return !_lines.IsEmpty; } }

        private readonly ConcurrentQueue<string> _lines = new ConcurrentQueue<string>();
        private Process _proc;
        private volatile bool _cancelled;

        /// <summary>启动管线。projectRoot = 仓库根 (openvsp-flightsim/)。
        /// extraArgs = 附加 CLI 参数 (如 "--surface-set thin --ref-set thin")。</summary>
        public void Start(string pythonExe, string projectRoot, string vspPath,
                          string name, float alpha, float mach,
                          float maxThrust, bool autoControls, string extraArgs = "")
        {
            if (IsRunning)
            {
                UnityEngine.Debug.LogWarning("[PipelineRunner] 已在运行");
                return;
            }
            IsRunning = true;
            _cancelled = false;
            OutDir = "";
            LastError = "";
            while (_lines.TryDequeue(out _)) { }   // 丢弃上一轮残留的 [done]/[ui] 结束, 否则会被误当本轮完成
            _lines.Enqueue("[ui] 启动导入: " + vspPath);

            var args = $"\"{projectRoot.Replace('\\', '/')}/Tools/import_vsp.py\" " +
                       $"--vsp \"{vspPath.Replace('\\', '/')}\" --name \"{name}\" " +
                       $"--alpha {alpha.ToString(System.Globalization.CultureInfo.InvariantCulture)} " +
                       $"--mach {mach.ToString(System.Globalization.CultureInfo.InvariantCulture)}";
            if (maxThrust > 0f)
                args += " --max-thrust " + maxThrust.ToString(
                    System.Globalization.CultureInfo.InvariantCulture);
            if (autoControls) args += " --auto-controls";
            if (!string.IsNullOrEmpty(extraArgs)) args += " " + extraArgs;

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

            var thread = new Thread(() => Run(psi)) { IsBackground = true };
            thread.Start();
        }

        private void Run(ProcessStartInfo psi)
        {
            try
            {
                _proc = new Process { StartInfo = psi };
                _proc.OutputDataReceived += (_, e) => { if (e.Data != null) _lines.Enqueue(e.Data); };
                _proc.ErrorDataReceived += (_, e) => { if (e.Data != null) _lines.Enqueue("[py] " + e.Data); };
                _proc.Start();
                _proc.BeginOutputReadLine();
                _proc.BeginErrorReadLine();
                _proc.WaitForExit();
                if (_proc.ExitCode != 0 && !_cancelled)
                    LastError = "退出码 " + _proc.ExitCode;
            }
            catch (System.Exception ex)
            {
                LastError = ex.Message;
            }
            finally
            {
                IsRunning = false;
                _lines.Enqueue(_cancelled ? "[ui] 已取消" : "[ui] 结束");
            }
        }

        /// <summary>主线程每帧调用: 取出累积的行回调。返回值 = 本批是否有新行。</summary>
        public bool Drain(System.Action<string> onLine)
        {
            bool any = false;
            while (_lines.TryDequeue(out string line))
            {
                any = true;
                if (line.StartsWith("[done]"))
                {
                    // "[done] 产物: D:/.../VspImports/X (用时 123s)"
                    int i = line.IndexOf(':');
                    int j = line.LastIndexOf('(');
                    if (i >= 0 && j > i)
                        OutDir = line.Substring(i + 1, j - i - 1).Trim();
                }
                onLine(line);
            }
            return any;
        }

        public void Cancel()
        {
            _cancelled = true;
            var p = _proc;
            if (p != null && !p.HasExited)
            {
                try
                {
                    // 杀整棵进程树 (python → vspscript → vspaero)
                    Process.Start("taskkill", $"/PID {p.Id} /T /F");
                }
                catch { /* 进程刚好退出 */ }
            }
        }
    }
}
