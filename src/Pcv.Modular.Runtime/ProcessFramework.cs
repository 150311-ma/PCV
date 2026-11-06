using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Pcv.Modular.Abstractions;

namespace Pcv.Modular.Runtime
{
    /// <summary>
    /// 进程型框架的运行实例。
    ///
    /// 协议刻意做得土：一行一条，普通行就是输出；以 "@@PCV " 开头的行才是控制消息（JSON）。
    /// 这样任何语言只要会 print 就能写框架，不用先实现一套 RPC 库——
    /// "支持近乎所有语言"靠的就是这一点，而不是给每种语言写 SDK。
    /// </summary>
    public sealed class ProcessFramework : IDisposable
    {
        private const string ControlPrefix = "@@PCV ";

        private readonly ModuleManifest _manifest;
        private readonly ILogger _log;
        private readonly string _dataDirectory;
        private readonly object _lock = new object();
        private Process _process;
        private FrameworkJob _job;
        private bool _stopping;

        public ProcessFramework(ModuleManifest manifest, ILogger log, string dataDirectory)
        {
            _manifest = manifest;
            _log = log;
            _dataDirectory = dataDirectory;
            Title = manifest.DisplayName;
        }

        public string Id => _manifest.Id;
        public string Title { get; private set; }
        public bool IsRunning
        {
            get
            {
                try
                {
                    return _process != null && !_process.HasExited;
                }
                catch
                {
                    return false;
                }
            }
        }

        public event Action<ProcessFramework, string> Output;
        public event Action<ProcessFramework, string> TitleChanged;
        public event Action<ProcessFramework, int> Exited;

        public void Start()
        {
            if (IsRunning) return;

            var directory = _manifest.DirectoryPath;
            var script = Path.Combine(directory, _manifest.Script ?? "");
            if (!File.Exists(script))
                throw new FileNotFoundException("找不到入口文件：" + _manifest.Script, script);

            var startInfo = FrameworkRuntime.BuildStartInfo(_manifest, script, directory, _dataDirectory);

            _process = new Process
            {
                StartInfo = startInfo,
                EnableRaisingEvents = true
            };
            _process.OutputDataReceived += (s, e) => OnLine(e.Data);
            _process.ErrorDataReceived += (s, e) =>
            {
                if (e.Data != null) OnLine("[stderr] " + e.Data);
            };
            _process.Exited += (s, e) =>
            {
                var code = -1;
                try { code = _process.ExitCode; } catch { }
                _log.Info($"{Id} 进程结束（退出码 {code}）");
                FrameworkRegistry.Unregister(Id);
                _job?.Dispose();
                _job = null;
                Exited?.Invoke(this, code);
            };

            if (!_process.Start())
                throw new InvalidOperationException("进程没有启动起来");

            // 不用 BeginOutputReadLine：它按 StandardOutputEncoding（UTF-8）硬解，
            // 而 Java 在中文 Windows 上是按 GBK 吐的，直接解就是一片乱码。
            // 自己读字节，先试 UTF-8，失败再退系统编码。
            var outThread = new System.Threading.Thread(() => Pump(_process.StandardOutput.BaseStream, false)) { IsBackground = true };
            var errThread = new System.Threading.Thread(() => Pump(_process.StandardError.BaseStream, true)) { IsBackground = true };
            outThread.Start();
            errThread.Start();

            // 给它套一根回收绳：宿主死了它跟着死，内存 1 GB、进程树最多 8 个
            _job = new FrameworkJob();
            if (_job.Attach(_process.Handle, 1024L * 1024 * 1024, 8))
                _log.Info($"{Id} 已纳入作业对象（内存上限 1 GB，最多 8 个进程）");
            else
                _log.Warn($"{Id} 没能纳入作业对象，宿主退出后可能留下孤儿进程");

            FrameworkRegistry.Register(this);

            SendControl(new JObject { ["type"] = "init", ["id"] = Id, ["dataDir"] = _dataDirectory, ["peers"] = new JArray(FrameworkRegistry.Ids.Where(x => x != Id)) });
            _log.Info($"{Id} 进程已启动（{startInfo.FileName}）");
        }

        /// <summary>给框架发一行普通文本（就是它的 stdin）。</summary>
        public void SendText(string text)
        {
            WriteLine(text ?? "");
        }

        /// <summary>给框架发一条控制消息。</summary>
        public void SendControl(JObject payload)
        {
            WriteLine(ControlPrefix + payload.ToString(Formatting.None));
        }

        public void Stop()
        {
            if (!IsRunning) return;
            _stopping = true;
            try
            {
                SendControl(new JObject { ["type"] = "stop" });
                if (!_process.WaitForExit(1500)) _process.Kill();
            }
            catch
            {
                // 已经退出了
            }
            finally
            {
                _stopping = false;
                FrameworkRegistry.Unregister(Id);
                _job?.Dispose();
                _job = null;
            }
        }

        private void WriteLine(string line)
        {
            try
            {
                lock (_lock)
                {
                    if (_process == null || _process.HasExited) return;

                    // 必须自己写 UTF-8 字节：.NET Framework 的 StandardInput 用 Encoding.Default（这里是 GBK），
                    // 而 Python 3 那边按 UTF-8 解——中文一进去就 UnicodeDecodeError，子进程直接退出。
                    // StandardInputEncoding 是 .NET Core 才有的，net48 只能绕过它的 StreamWriter。
                    var bytes = Encoding.UTF8.GetBytes(line + "\n");
                    _process.StandardInput.BaseStream.Write(bytes, 0, bytes.Length);
                    _process.StandardInput.BaseStream.Flush();
                }
            }
            catch (Exception ex)
            {
                _log.Warn($"{Id} 写入失败：{ex.Message}");
            }
        }

        private static readonly UTF8Encoding StrictUtf8 = new UTF8Encoding(false, true);

        /// <summary>按字节读，按行切，解码时 UTF-8 优先、GBK 兜底。</summary>
        private void Pump(Stream stream, bool isError)
        {
            var buffer = new byte[4096];
            var pending = new List<byte>(4096);

            try
            {
                int read;
                while ((read = stream.Read(buffer, 0, buffer.Length)) > 0)
                {
                    for (var i = 0; i < read; i++)
                    {
                        var value = buffer[i];
                        if (value == (byte)'\n')
                        {
                            EmitDecoded(pending, isError);
                            pending.Clear();
                        }
                        else if (value != (byte)'\r')
                        {
                            pending.Add(value);
                        }
                    }
                }
                if (pending.Count > 0) EmitDecoded(pending, isError);
            }
            catch (Exception)
            {
                // 进程退出时流会断，正常现象
            }
        }

        private void EmitDecoded(List<byte> bytes, bool isError)
        {
            if (bytes.Count == 0) return;

            var data = bytes.ToArray();
            string text;
            try
            {
                text = StrictUtf8.GetString(data);
            }
            catch (DecoderFallbackException)
            {
                // 不是合法 UTF-8 —— 大概率是 GBK（java / 某些控制台程序）
                text = Encoding.Default.GetString(data);
            }

            OnLine(isError ? "[stderr] " + text : text);
        }

        private void OnLine(string line)
        {
            if (line == null) return;

            // 子进程说什么都留一份在宿主日志里：窗口没弹出来时，这是唯一的线索
            if (line.StartsWith("[stderr]", StringComparison.Ordinal))
                _log.Warn($"[{Id}] {line}");

            if (line.StartsWith(ControlPrefix, StringComparison.Ordinal))
            {
                HandleControl(line.Substring(ControlPrefix.Length));
                return;
            }

            Output?.Invoke(this, line);
        }

        private void HandleControl(string json)
        {
            JObject payload;
            try
            {
                payload = JObject.Parse(json);
            }
            catch
            {
                Output?.Invoke(this, "[协议] 看不懂的控制消息：" + json);
                return;
            }

            switch ((string)payload["type"])
            {
                case "title":
                {
                    var title = (string)payload["text"];
                    if (!string.IsNullOrWhiteSpace(title))
                    {
                        Title = title;
                        TitleChanged?.Invoke(this, title);
                    }
                    break;
                }
                case "call":
                {
                    // 框架之间的互调：宿主只做转发，两边都不知道对方的进程信息
                    var target = (string)payload["target"];
                    var peer = FrameworkRegistry.Find(target);
                    if (peer == null)
                    {
                        SendControl(new JObject { ["type"] = "callFailed", ["target"] = target, ["reason"] = "没有这个框架在运行" });
                        return;
                    }
                    peer.SendControl(new JObject
                    {
                        ["type"] = "peer",
                        ["from"] = Id,
                        ["text"] = (string)payload["text"]
                    });
                    break;
                }
                case "reply":
                {
                    var target = (string)payload["target"];
                    FrameworkRegistry.Find(target)?.SendControl(new JObject
                    {
                        ["type"] = "peer",
                        ["from"] = Id,
                        ["text"] = (string)payload["text"]
                    });
                    break;
                }
                case "log":
                    _log.Info($"[{Id}] {(string)payload["text"]}");
                    break;
                default:
                    Output?.Invoke(this, "[协议] 不认识的消息：" + payload["type"]);
                    break;
            }
        }

        public void Dispose() => Stop();
    }

    /// <summary>正在运行的进程型框架登记处。互调全靠它做路由。</summary>
    public static class FrameworkRegistry
    {
        private static readonly Dictionary<string, ProcessFramework> Running =
            new Dictionary<string, ProcessFramework>(StringComparer.OrdinalIgnoreCase);

        public static void Register(ProcessFramework framework)
        {
            lock (Running) Running[framework.Id] = framework;
        }

        public static void Unregister(string id)
        {
            lock (Running) Running.Remove(id);
        }

        public static ProcessFramework Find(string id)
        {
            if (string.IsNullOrWhiteSpace(id)) return null;
            lock (Running)
            {
                Running.TryGetValue(id, out var found);
                return found;
            }
        }

        public static IReadOnlyList<string> Ids
        {
            get
            {
                lock (Running) return Running.Keys.ToList();
            }
        }
    }

    /// <summary>
    /// 把清单里的 runtime 名字翻译成真正的可执行文件，并拼出启动参数。
    ///
    /// 这里只做"能启动"，不做权限降级。要真的挡住它读主机文件，需要 AppContainer
    /// 或另一个低权限账户，这轮没做——与其假装隔离，不如把边界说清楚：
    /// 框架拿不到宿主的任何对象，只能通过 stdin/stdout 的白名单消息请求宿主代劳。
    /// </summary>
    public static class FrameworkRuntime
    {
        public static ProcessStartInfo BuildStartInfo(ModuleManifest manifest, string scriptPath,
            string directory, string dataDirectory)
        {
            var runtime = (manifest.Runtime ?? "").Trim().ToLowerInvariant();

            var info = new ProcessStartInfo
            {
                WorkingDirectory = directory,
                UseShellExecute = false,
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
                StandardOutputEncoding = Encoding.UTF8,
                StandardErrorEncoding = Encoding.UTF8
            };

            // 注意：这里不碰 EnvironmentVariables。
            // 本机环境里同时存在 HTTPS_PROXY 和 https_proxy，ProcessStartInfo 内部那个
            // 大小写不敏感的字典会直接抛"已添加项"。所以数据目录和 id 走命令行参数，
            // 顺便也让脚本更容易调试（直接命令行跑也能带上同样参数）。
            var context = $"\"{dataDirectory}\" \"{manifest.Id}\"";

            switch (runtime)
            {
                case "python":
                    info.FileName = ResolvePython();
                    // -u：不走缓冲，print 立刻出来，不然窗口半天没动静
                    info.Arguments = "-u " + Quote(scriptPath) + " " + context;
                    break;
                case "node":
                    info.FileName = ResolveFromPath("node.exe") ?? "node.exe";
                    info.Arguments = Quote(scriptPath) + " " + context;
                    break;
                case "exec":
                case "exe":
                    info.FileName = scriptPath;
                    info.Arguments = (manifest.Arguments ?? "").Replace("{dir}", directory);
                    break;
                default:
                    throw new NotSupportedException($"不认识的 runtime：{manifest.Runtime}（支持 python / node / exec）");
            }

            return info;
        }

        private static string ResolvePython()
        {
            var found = ResolveFromPath("python.exe");
            if (found != null) return found;

            // 微软商店的 Python 会放一个 py.exe 在 Windows 目录
            var launcher = ResolveFromPath("py.exe");
            if (launcher != null) return launcher;

            var local = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "Programs", "Python");
            if (Directory.Exists(local))
            {
                var candidate = Directory.GetDirectories(local)
                    .OrderByDescending(d => d)
                    .Select(d => Path.Combine(d, "python.exe"))
                    .FirstOrDefault(File.Exists);
                if (candidate != null) return candidate;
            }

            return "python.exe";
        }

        private static string ResolveFromPath(string fileName)
        {
            var path = Environment.GetEnvironmentVariable("PATH") ?? "";
            foreach (var folder in path.Split(';'))
            {
                if (string.IsNullOrWhiteSpace(folder)) continue;
                try
                {
                    var candidate = Path.Combine(folder.Trim('"'), fileName);
                    if (File.Exists(candidate)) return candidate;
                }
                catch
                {
                    // 路径里有非法字符就跳过
                }
            }
            return null;
        }

        private static string Quote(string value) => "\"" + value + "\"";
    }
}
