using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Threading;
using UnityEngine;

namespace FlightSim
{
    /// <summary>
    /// 机型工坊 W1: 常驻 Python worker 客户端 (JSON-Lines over stdio, 提示词 §9)。
    /// 协议: 请求 {id,cmd,args} / 响应 {id,ok,data|error} / 推送 {event:"log"}。
    /// 后台线程读行进 ConcurrentQueue, 主线程 Poll 分发 (OnGUI 只能主线程)。
    /// worker 必须 Python 3.13 (_vsp.pyd 绑定), 默认自动探测, 可用 PlayerPrefs
    /// "Workshop.Python313" 覆盖; VSPROOT 用 PlayerPrefs "Workshop.VspRoot" 覆盖。
    /// 默认超时 30s (提示词 §3.4), 超时/崩溃不静默: LastError + Restart()。
    /// </summary>
    public class WorkshopWorkerClient : IDisposable
    {
        public const float DefaultTimeoutSec = 30f;

        public bool IsAlive { get { return _proc != null && !_proc.HasExited; } }
        public string LastError { get; private set; } = "";
        public string PythonExe { get; private set; } = "";
        /// <summary>最近一次成功响应的耗时 (ms), 供 UI 显示延迟。</summary>
        public float LastRoundtripMs { get; private set; } = -1f;
        /// <summary>最近一次成功应答的时刻 (Time.realtimeSinceStartup); -9999=从未。
        /// U8 自愈判据: worker IsAlive 但此值长时间不更新 = 挂死, 由 UI 决策重启。</summary>
        public float LastReplyAt { get; private set; } = -9999f;

        private Process _proc;
        private Thread _reader;
        private readonly ConcurrentQueue<string> _lines = new ConcurrentQueue<string>();
        private readonly ConcurrentQueue<string> _stderr = new ConcurrentQueue<string>();
        private readonly Dictionary<int, Action<JsonReply>> _pending = new Dictionary<int, Action<JsonReply>>();
        private readonly List<(float deadline, int id, float sec)> _timeouts = new List<(float, int, float)>();
        private int _nextId;
        private bool _disposed;

        /// <summary>最近 40 行 worker stderr (OpenVSP C printf), 诊断用。</summary>
        public string StderrTail
        {
            get { return string.Join("\n", _stderr.ToArray()); }
        }

        [Serializable]
        public struct JsonReply
        {
            public int id;          // id=-1 表示无对应请求的失败响应
            public bool ok;
            public string error;
            public string dataJson; // data 原始 JSON (JsonUtility 按需再解析)
            public float ms;
        }

        // ---------- 进程管理 ----------
        public static string DefaultPython313()
        {
            string[] cands =
            {
                // 安装版捆绑的 Python (安装根/Python/python.exe; 开发机上不存在则跳过)
                Path.GetFullPath(Path.Combine(Application.dataPath, "..", "..",
                                              "Python", "python.exe")),
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                             @"Programs\Python\Python313\python.exe"),
                @"C:\Python313\python.exe",
            };
            foreach (string c in cands)
                if (File.Exists(c)) return c;
            return "python";   // 兜底 (PATH 上的 python 必须是 3.13 才能工作)
        }

        /// <summary>启动 worker。projectRoot = 仓库根 (openvsp-flightsim/)。</summary>
        public bool Start(string projectRoot)
        {
            if (IsAlive) return true;
            LastError = "";
            PythonExe = PlayerPrefs.GetString("Workshop.Python313", DefaultPython313());
            string script = Path.Combine(projectRoot, "Tools", "workshop_worker.py").Replace('\\', '/');
            if (!File.Exists(script))
            {
                LastError = "找不到 worker 脚本: " + script;
                return false;
            }

            var psi = new ProcessStartInfo
            {
                FileName = PythonExe,
                Arguments = "\"" + script + "\"",
                UseShellExecute = false,
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
                StandardOutputEncoding = System.Text.Encoding.UTF8,
                StandardErrorEncoding = System.Text.Encoding.UTF8,
                WorkingDirectory = projectRoot,
            };
            psi.EnvironmentVariables["PYTHONIOENCODING"] = "utf-8";
            string vspRoot = PlayerPrefs.GetString("Workshop.VspRoot", "");
            if (!string.IsNullOrEmpty(vspRoot))
                psi.EnvironmentVariables["VSPROOT"] = vspRoot;

            try
            {
                _proc = new Process { StartInfo = psi, EnableRaisingEvents = true };
                _proc.Exited += (s, e) => { if (!_disposed && !StopRequested) LastError = "worker 进程已退出"; };
                _proc.Start();
                // U8 修复: stderr (worker 的 C printf 全走这) 必须排空 — 只重定向不读,
                // 管道缓冲 (~64KB) 写满后 worker 的 printf 在 C 层阻塞, 表现为
                // "改几次外形参数后所有请求超时" (worker 挂起且 CPU 为零)。
                // 只保留尾部 40 行供超时/崩溃诊断。
                _proc.ErrorDataReceived += (s, e) =>
                {
                    if (string.IsNullOrEmpty(e.Data)) return;
                    _stderr.Enqueue(e.Data);
                    while (_stderr.Count > 40 && _stderr.TryDequeue(out _)) { }
                };
                _proc.BeginErrorReadLine();
            }
            catch (System.Exception ex)
            {
                LastError = "worker 启动失败: " + ex.Message;
                return false;
            }

            _reader = new Thread(ReadLoop) { IsBackground = true };
            _reader.Start();
            return true;
        }

        private bool StopRequested { get; set; }

        private void ReadLoop()
        {
            var proc = _proc;
            if (proc == null) return;
            try
            {
                string line;
                while ((line = proc.StandardOutput.ReadLine()) != null)
                    _lines.Enqueue(line);
            }
            catch { /* 进程关闭 */ }
        }

        /// <summary>主线程每帧调用: 分发响应/日志/超时。返回是否有新事件。</summary>
        public void Poll(Action<string> onLog, Action<JsonReply> onTimeout)
        {
            while (_lines.TryDequeue(out string line))
            {
                string evt = WorkshopJson.Str(line, "event");
                if (!string.IsNullOrEmpty(evt))
                {
                    onLog?.Invoke(WorkshopJson.Str(line, "msg") ?? "");
                    continue;
                }
                int id = WorkshopJson.Int(line, "id", -1);
                if (id > 0 && _pending.TryGetValue(id, out var cb))
                {
                    _pending.Remove(id);
                    _timeouts.RemoveAll(t => t.id == id);
                    LastRoundtripMs = WorkshopJson.Num(line, "ms", -1f);
                    LastReplyAt = Time.realtimeSinceStartup;   // U8 自愈判据
                    cb(new JsonReply
                    {
                        id = id,
                        ok = WorkshopJson.Bool(line, "ok"),
                        error = WorkshopJson.Str(line, "error") ?? "",
                        dataJson = WorkshopJson.Raw(line, "data") ?? "{}",
                        ms = WorkshopJson.Num(line, "ms", -1f),
                    });
                }
                else if (id <= 0 && !WorkshopJson.Bool(line, "ok"))
                {
                    onLog?.Invoke("[worker] " + (WorkshopJson.Str(line, "error") ?? line));
                }
            }

            // 超时扫描
            float now = Time.realtimeSinceStartup;
            for (int i = _timeouts.Count - 1; i >= 0; i--)
            {
                if (now > _timeouts[i].deadline)
                {
                    int tid = _timeouts[i].id;
                    float tsec = _timeouts[i].sec;
                    _timeouts.RemoveAt(i);
                    if (_pending.TryGetValue(tid, out var cb))
                    {
                        _pending.Remove(tid);
                        string tail = StderrTail;
                        onTimeout?.Invoke(new JsonReply
                        {
                            id = tid, ok = false,
                            error = "请求超时 (" + tsec.ToString("0") + "s)" +
                                (tail.Length > 0 ? " | stderr 尾部: " + tail.Substring(System.Math.Max(0, tail.Length - 300)) : ""),
                        });
                    }
                }
            }
        }

        /// <summary>异步请求。onReply 在主线程 Poll 中回调 (ok=false 时 error 有值)。
        /// timeoutSec: 重命令 (mass_props/delete_hidden/save/export_meshes 对复杂
        /// 模型可超 30s) 传更长; 默认 30s (U7)。</summary>
        public int Call(string cmd, string argsJson, Action<JsonReply> onReply,
                        float timeoutSec = DefaultTimeoutSec)
        {
            if (!IsAlive)
            {
                onReply?.Invoke(new JsonReply { id = -1, ok = false, error = "worker 未运行: " + LastError });
                return -1;
            }
            int id = ++_nextId;
            // dataJson 在响应侧从原始行解析 (见 Poll 的 data 抽取), 请求侧只组 {id,cmd,args}
            string req = "{\"id\":" + id + ",\"cmd\":\"" + cmd + "\",\"args\":" +
                         (string.IsNullOrEmpty(argsJson) ? "{}" : argsJson) + "}";
            try
            {
                _proc.StandardInput.WriteLine(req);
                _proc.StandardInput.Flush();
            }
            catch (System.Exception ex)
            {
                onReply?.Invoke(new JsonReply { id = id, ok = false, error = "写入失败: " + ex.Message });
                return id;
            }
            if (onReply != null)
            {
                _pending[id] = onReply;
                _timeouts.Add((Time.realtimeSinceStartup + timeoutSec, id, timeoutSec));
            }
            return id;
        }

        /// <summary>优雅关闭 (shutdown 命令 + 进程退出); 供工坊/场景销毁时调用。</summary>
        public void Dispose()
        {
            _disposed = true;
            StopRequested = true;
            try
            {
                if (IsAlive)
                {
                    _proc.StandardInput.WriteLine("{\"id\":99999,\"cmd\":\"shutdown\"}");
                    _proc.StandardInput.Flush();
                    if (!_proc.WaitForExit(3000))
                        try { Process.Start("taskkill", "/PID " + _proc.Id + " /T /F"); } catch { }
                }
            }
            catch { }
            try { if (_proc != null) _proc.Dispose(); } catch { }
            _proc = null;
        }

        /// <summary>异常后重启 worker (提示词 §3.4 「重启 OpenVSP 服务」)。</summary>
        public bool Restart(string projectRoot)
        {
            Dispose();
            _disposed = false;
            StopRequested = false;
            return Start(projectRoot);
        }
    }
}
