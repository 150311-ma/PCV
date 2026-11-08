using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows.Threading;

namespace Pcv.Shell
{
    /// <summary>
    /// 检出的一类问题。Level 决定要不要拦下用户。
    /// </summary>
    internal enum ThreatLevel
    {
        /// <summary>只是记一笔，不打扰用户。</summary>
        Notice,
        /// <summary>确认过就会强制结束进程。</summary>
        Fatal,
    }

    /// <summary>
    /// 一条检出。Title 给用户看，Detail 说明是哪一项、能做什么。
    /// </summary>
    internal sealed class Threat
    {
        public Threat(ThreatLevel level, string title, string detail)
        {
            Level = level;
            Title = title;
            Detail = detail;
        }

        public ThreatLevel Level { get; }
        public string Title { get; }
        public string Detail { get; }
    }

    /// <summary>
    /// 简单的安全系统：盯着「主机被强制改动」这四件事。
    ///
    /// 为什么是这四件：
    ///   1. 调试器挂上了 —— 有人正在看、甚至改我们的内存；
    ///   2. 进程里冒出来不是系统的模块 —— 有人往里塞了东西；
    ///   3. 运行环境被塞了探针（CLR profiler / 启动钩子）—— 这类环境变量
    ///      能在不改任何文件的情况下改变整个进程的走向；
    ///   4. 我们自己的文件被动过 —— 指纹对不上。
    ///
    /// 边界要说明白：这挡的是「顺手就能改」的层次，不是专业逆向。
    /// 真正值钱的东西（凭证怎么算、密钥是什么）根本不在客户端，
    /// 服务器算完只回一个短码，所以本地被看穿也拿不到什么。
    ///
    /// 检出之后的动作只有一种：弹窗把话说清楚，用户点「确定」就结束进程。
    /// 不偷偷继续跑，也不偷偷上报。
    /// </summary>
    internal static class SecurityGuard
    {
        /// <summary>内嵌的资源清单名（由 tools/build_integrity.py 生成）。</summary>
        private const string IntegrityResource = "PCV.integrity.json";

        /// <summary>周期检查的间隔（毫秒）。</summary>
        private const int CheckIntervalMs = 10000;

        private static readonly object Gate = new object();
        private static HashSet<string> _baselineModules;
        private static DispatcherTimer _timer;
        private static bool _terminating;

        // ── 外部状态检查用的 Win32 声明 ────────────────────────────────

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool CheckRemoteDebuggerPresent(IntPtr hProcess, out bool isDebuggerPresent);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool ReadProcessMemory(IntPtr hProcess, IntPtr baseAddress,
            byte[] buffer, int size, out IntPtr bytesRead);

        [StructLayout(LayoutKind.Sequential)]
        private struct ProcessBasicInformation
        {
            public IntPtr Reserved1;
            public IntPtr PebBaseAddress;
            public IntPtr Reserved2_0;
            public IntPtr Reserved2_1;
            public IntPtr UniqueProcessId;
            public IntPtr Reserved3;
        }

        [DllImport("ntdll.dll")]
        private static extern int NtQueryInformationProcess(IntPtr processHandle, int informationClass,
            ref ProcessBasicInformation information, int informationLength, out int returnLength);

        /// <summary>
        /// 启动早期调用一次：先记住「干净时进程里有哪些模块」，再查一遍。
        /// 必须赶在第三方程序集被加载之前。
        /// </summary>
        public static void Initialize()
        {
            lock (Gate)
            {
                if (_baselineModules != null) return;
                _baselineModules = SnapshotModules();
            }

            var threats = Inspect();
            if (threats.Count > 0) Report(threats);

            _timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(CheckIntervalMs) };
            _timer.Tick += (s, e) =>
            {
                var found = Inspect();
                if (found.Count > 0) Report(found);
            };
            _timer.Start();
        }

        /// <summary>只查不处置，供自检窗口调用。</summary>
        public static List<Threat> Inspect()
        {
            var threats = new List<Threat>();
            try { threats.AddRange(CheckDebugger()); }
            catch { /* 检查本身失败不算威胁 */ }
            try { threats.AddRange(CheckForeignModules()); }
            catch { }
            try { threats.AddRange(CheckRuntimeHooks()); }
            catch { }
            try { threats.AddRange(CheckIntegrity()); }
            catch { }
            return threats;
        }

        // ── 1. 调试器 ──────────────────────────────────────────────────────

        private static IEnumerable<Threat> CheckDebugger()
        {
            var found = new List<Threat>();

            if (Debugger.IsAttached)
            {
                found.Add(new Threat(ThreatLevel.Fatal, "调试器已挂上",
                    "有人在用调试器连着 PCV，可以直接读写它的内存。请关掉调试器再启动。"));
                return found;
            }

            bool remote = false;
            try
            {
                if (CheckRemoteDebuggerPresent(Process.GetCurrentProcess().Handle, out remote) && remote)
                {
                    found.Add(new Threat(ThreatLevel.Fatal, "检测到远程调试器",
                        "另一个进程正连着 PCV 做调试。请关掉那个程序再启动。"));
                    return found;
                }
            }
            catch { }

            var flag = ReadNtGlobalFlag();
            if (flag != 0)
            {
                found.Add(new Threat(ThreatLevel.Fatal, "进程被调试器改造过",
                    "系统的调试标记说明 PCV 的运行方式被改过（0x" + flag.ToString("X")
                    + "）。请关掉调试器或注入工具再启动。"));
            }
            return found;
        }

        /// <summary>
        /// 读 PEB 的 NtGlobalFlag。挂了调试器 / 某些注入器会把它置成非 0。
        /// 64 位与 32 位的偏移不一样：x64 是 0xBC，x86 是 0x68。
        /// </summary>
        private static uint ReadNtGlobalFlag()
        {
            var info = new ProcessBasicInformation();
            int returned;
            var status = NtQueryInformationProcess(Process.GetCurrentProcess().Handle, 0,
                ref info, Marshal.SizeOf(info), out returned);
            if (status != 0 || info.PebBaseAddress == IntPtr.Zero) return 0;

            var offset = IntPtr.Size == 8 ? 0xBC : 0x68;
            var buffer = new byte[4];
            IntPtr read;
            if (!ReadProcessMemory(Process.GetCurrentProcess().Handle,
                    new IntPtr(info.PebBaseAddress.ToInt64() + offset), buffer, 4, out read))
                return 0;
            return BitConverter.ToUInt32(buffer, 0);
        }

        // ── 2. 外来模块 ────────────────────────────────────────────────────

        private static HashSet<string> SnapshotModules()
        {
            var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            try
            {
                foreach (ProcessModule module in Process.GetCurrentProcess().Modules)
                {
                    var path = SafeModulePath(module);
                    if (!string.IsNullOrEmpty(path)) names.Add(path);
                }
            }
            catch { }
            return names;
        }

        private static string SafeModulePath(ProcessModule module)
        {
            try { return module.FileName; }
            catch { return null; }
        }

        /// <summary>
        /// 干净时记下的模块之外，又冒出来一个不属于系统目录、也不属于 PCV
        /// 自己目录的 dll —— 那多半是被人塞进来的。
        /// </summary>
        private static IEnumerable<Threat> CheckForeignModules()
        {
            HashSet<string> baseline;
            lock (Gate) baseline = _baselineModules;
            if (baseline == null) yield break;

            var current = SnapshotModules();
            var intruders = current.Where(p => !baseline.Contains(p)
                                             && !IsSystemPath(p)
                                             && !IsAppPath(p)).ToList();
            if (intruders.Count == 0) yield break;

            yield return new Threat(ThreatLevel.Fatal, "进程里被塞进了外部模块",
                "下面这些文件不属于 Windows，也不属于 PCV 自己的目录，却出现在 PCV 的进程里：\n"
                + string.Join("\n", intruders.Take(4))
                + "\n请关掉注入类工具后重新启动 PCV。");
        }

        private static bool IsSystemPath(string path)
        {
            var windows = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
            return path.StartsWith(windows, StringComparison.OrdinalIgnoreCase);
        }

        private static bool IsAppPath(string path)
        {
            var baseDirectory = AppDomain.CurrentDomain.BaseDirectory;
            return path.StartsWith(baseDirectory, StringComparison.OrdinalIgnoreCase);
        }

        // ── 3. 运行环境被挂钩子 ────────────────────────────────────────────

        /// <summary>
        /// 这几个环境变量能在「不改任何文件」的前提下改变整个进程的走向：
        /// CORECLR_PROFILER / CORECLR_PROFILER_PATH 会让 CLR 加载任意 dll，
        /// DOTNET_STARTUP_HOOKS 会在启动时执行任意代码。
        /// 正常的用户机器上不会有它们。
        /// </summary>
        private static readonly string[] HookVariables =
        {
            "CORECLR_PROFILER",
            "CORECLR_PROFILER_PATH",
            "CORECLR_PROFILER_PATH_64",
            "CORECLR_ENABLE_PROFILING",
            "COR_PROFILER",
            "COR_PROFILER_PATH",
            "COR_PROFILER_PATH_64",
            "DOTNET_STARTUP_HOOKS",
            "DOTNET_ADDITIONAL_DEPS",
            "COMPlus_ReadyToRun",
        };

        private static IEnumerable<Threat> CheckRuntimeHooks()
        {
            var hits = new List<string>();
            foreach (var name in HookVariables)
            {
                string value = null;
                try { value = Environment.GetEnvironmentVariable(name); }
                catch { }
                if (!string.IsNullOrWhiteSpace(value)) hits.Add(name);
            }

            // COMPlus_ 前缀整体都是给 CLR 改行为的，一律算异常
            try
            {
                foreach (System.Collections.DictionaryEntry entry in Environment.GetEnvironmentVariables())
                {
                    var key = entry.Key as string;
                    if (key == null) continue;
                    if (!key.StartsWith("COMPlus_", StringComparison.OrdinalIgnoreCase)) continue;
                    if (hits.Contains(key)) continue;
                    hits.Add(key);
                }
            }
            catch { }

            if (hits.Count == 0) yield break;

            yield return new Threat(ThreatLevel.Fatal, "运行环境里被塞了调试挂钩",
                "下面这些环境变量会改变 PCV 进程的行为（能加载外部代码）：\n"
                + string.Join("\n", hits.Take(6))
                + "\n请清掉它们再启动 PCV。");
        }

        // ── 4. 文件指纹 ────────────────────────────────────────────────────

        /// <summary>
        /// 与清单里的 sha256 比对。清单由 tools/build_integrity.py 在
        /// 发布时算好，放在程序同目录；本地没生成清单时这一项直接跳过。
        /// </summary>
        private static IEnumerable<Threat> CheckIntegrity()
        {
            var baseDirectory = AppDomain.CurrentDomain.BaseDirectory;
            var entries = IntegrityManifest.LoadFromDisk(baseDirectory);
            if (entries.Count == 0) yield break;

            var broken = new List<string>();
            foreach (var entry in entries)
            {
                var path = Path.Combine(baseDirectory, entry.Path.Replace('/', Path.DirectorySeparatorChar));
                if (!File.Exists(path)) { broken.Add(entry.Path + "（不见了）"); continue; }
                // 比清单还新的文件是本地刚编译出来的，不是被人改的 —— 跳过
                if (!IntegrityManifest.ManifestIsNewerThan(baseDirectory, path)) continue;
                try
                {
                    var actual = IntegrityManifest.HashOf(path);
                    if (!string.Equals(actual, entry.Sha256, StringComparison.OrdinalIgnoreCase))
                        broken.Add(entry.Path + "（内容变了）");
                }
                catch { }
            }

            if (broken.Count == 0) yield break;

            yield return new Threat(ThreatLevel.Fatal, "PCV 的文件被改过",
                "下面这些文件的指纹和发布时对不上：\n"
                + string.Join("\n", broken.Take(5))
                + "\n请从官方渠道重新下载 PCV。");
        }

        private static string ReadEmbedded(string name)
        {
            try
            {
                var assembly = Assembly.GetExecutingAssembly();
                using (var stream = assembly.GetManifestResourceStream(name))
                {
                    if (stream == null) return null;
                    using (var reader = new StreamReader(stream, Encoding.UTF8))
                        return reader.ReadToEnd();
                }
            }
            catch { return null; }
        }

        // ── 处置 ────────────────────────────────────────────────────────────
        /// <summary>
        /// 把检出说给用户听。用户点「确定」就结束进程 —— 这是唯一出口，
        /// 没有「忽略」「以后再说」这种选项。
        /// </summary>
        private static void Report(List<Threat> threats)
        {
            var fatal = threats.Where(t => t.Level == ThreatLevel.Fatal).ToList();
            if (fatal.Count == 0) return;

            lock (Gate)
            {
                if (_terminating) return;
                _terminating = true;
            }

            try
            {
                _timer?.Stop();
            }
            catch { }

            // 检出即落日志：弹窗在等用户点击时进程是活的，
            // 出了事要能查得到当时到底检出了什么。
            WriteLog("检出 " + fatal.Count + " 项异常："
                     + string.Join("；", fatal.Select(t => t.Title)));

            var window = new SecurityAlertWindow(fatal);
            try { window.ShowDialog(); }
            catch { /* 弹不出来就直接结束，不留后门 */ }

            Terminate();
        }

        /// <summary>把一条记录写进 logs/security.log。写不了就算了。</summary>
        private static void WriteLog(string message)
        {
            try
            {
                var folder = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "logs");
                Directory.CreateDirectory(folder);
                File.AppendAllText(Path.Combine(folder, "security.log"),
                    "==== " + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") + " ====\r\n"
                    + message + Environment.NewLine);
            }
            catch { }
        }

        /// <summary>强制结束 PCV 进程。</summary>
        public static void Terminate()
        {
            WriteLog("用户确认后结束进程。");

            try
            {
                Process.GetCurrentProcess().Kill();
            }
            catch
            {
                Environment.Exit(9);
            }
        }
    }
}
