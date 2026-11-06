using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using Pcv.Modular.Abstractions;
using Pcv.Modular.Host;

namespace Pcv.Modular.Cli
{
    internal static class Program
    {
        private static int Main(string[] args)
        {
            Console.OutputEncoding = System.Text.Encoding.UTF8;
            var modulesDirectory = Directory.GetCurrentDirectory();
            var faultInjection = false;
            var packageSelfTest = false;
            var overwrite = false;
            string installPath = null, uninstallId = null;
            var listOnly = false;

            for (var i = 0; i < args.Length; i++)
            {
                switch (args[i])
                {
                    case "--modules":
                        if (i + 1 < args.Length) modulesDirectory = args[++i];
                        break;
                    case "--inject-fault":
                        faultInjection = true;
                        break;
                    case "--package-selftest":
                        packageSelfTest = true;
                        break;
                    case "--install":
                        if (i + 1 < args.Length) installPath = args[++i];
                        break;
                    case "--overwrite":
                        overwrite = true;
                        break;
                    case "--list":
                        listOnly = true;
                        break;
                    case "--uninstall":
                        if (i + 1 < args.Length) uninstallId = args[++i];
                        break;
                    case "--help":
                    case "-h":
                        PrintHelp();
                        return 0;
                    default:
                        modulesDirectory = args[i];
                        break;
                }
            }

            if (faultInjection) return FaultInjectionSuite.Run();
            if (packageSelfTest) return PackageCommands.SelfTest();
            if (installPath != null) return PackageCommands.Install(modulesDirectory, installPath, overwrite);
            if (listOnly) return PackageCommands.List(modulesDirectory);
            if (uninstallId != null) return PackageCommands.Uninstall(modulesDirectory, uninstallId);
            return RunModules(modulesDirectory);
        }

        private static void PrintHelp()
        {
            Console.WriteLine("PCL 模块加载器验证工具");
            Console.WriteLine("  Pcv.Modular.Cli [模块目录]            加载并打印模块表（默认 ./modules）");
            Console.WriteLine("  Pcv.Modular.Cli --modules <目录>      同上，显式指定目录");
            Console.WriteLine("  Pcv.Modular.Cli --inject-fault        跑故障注入用例，验证加载器能识别各类坏模块");
            Console.WriteLine("  Pcv.Modular.Cli --modules <目录> --install <包> [--overwrite]   安装功能框架包");
            Console.WriteLine("  Pcv.Modular.Cli --modules <目录> --list                         列出已装框架");
            Console.WriteLine("  Pcv.Modular.Cli --modules <目录> --uninstall <id>                卸载框架");
            Console.WriteLine("  Pcv.Modular.Cli --package-selftest    跑安装器故障注入（坏包、zip slip、覆盖、回滚）");
            Console.WriteLine("退出码：0 健康 / 1 有失败或跳过 / 2 一个模块都没发现");
        }

        private static int RunModules(string modulesDirectory)
        {
            Console.WriteLine($"== 加载 {Path.GetFullPath(modulesDirectory)} ==");
            Console.WriteLine();

            using (var host = new ModuleHost(new ModuleHostOptions
            {
                ModulesDirectory = modulesDirectory,
                EchoToConsole = false
            }))
            {
                var report = host.Run();
                Console.WriteLine(report.Render());
                host.StopModules();
                return report.ExitCode;
            }
        }
    }

    /// <summary>
    /// 校验器本身也要被校验：这里主动造出每一类坏模块，确认加载器会按预期报红。
    /// 任何一个用例没红，就说明加载器该修的检查项漏了。
    /// </summary>
    internal static class FaultInjectionSuite
    {
        public static int Run()
        {
            var root = Path.Combine(Path.GetTempPath(), "pcl-modular-fixtures", Guid.NewGuid().ToString("N").Substring(0, 8));
            Directory.CreateDirectory(root);

            var self = typeof(FaultInjectionSuite).Assembly.Location;
            var checks = new List<(string Name, bool Passed, string Detail)>();

            // 1. JSON 语法错误
            WriteModule(root, "broken-json", "{ not json !!!", self);
            // 2. 清单合法但程序集不存在
            WriteManifest(root, "missing-assembly", "pcl.fixture.missing", "1.0.0", "NotExists.dll", null, self);
            // 3. OnLoad 抛异常
            WriteManifest(root, "boom", "pcl.fixture.boom", "1.0.0", self, "Pcv.Modular.Cli.BoomModule", self);
            // 4. 依赖失败模块 → 应被跳过
            WriteManifest(root, "dependent", "pcl.fixture.dependent", "1.0.0", self, "Pcv.Modular.Cli.GoodFixtureModule", self, new[] { "pcl.fixture.boom" });
            // 5. 循环依赖
            WriteManifest(root, "cycle-a", "pcl.fixture.cycle.a", "1.0.0", self, "Pcv.Modular.Cli.GoodFixtureModule", self, new[] { "pcl.fixture.cycle.b" });
            WriteManifest(root, "cycle-b", "pcl.fixture.cycle.b", "1.0.0", self, "Pcv.Modular.Cli.GoodFixtureModule", self, new[] { "pcl.fixture.cycle.a" });
            // 6. 重复 Id（两份，版本不同）
            WriteManifest(root, "dup-old", "pcl.fixture.dup", "0.9.0", self, "Pcv.Modular.Cli.GoodFixtureModule", self);
            WriteManifest(root, "dup-new", "pcl.fixture.dup", "1.1.0", self, "Pcv.Modular.Cli.GoodFixtureModule", self);
            // 7. 正常模块
            WriteManifest(root, "good", "pcl.fixture.good", "1.0.0", self, "Pcv.Modular.Cli.GoodFixtureModule", self);

            using (var host = new ModuleHost(new ModuleHostOptions
            {
                ModulesDirectory = root,
                EchoToConsole = false,
                LogFile = Path.Combine(root, "run.log")
            }))
            {
                var report = host.Run();
                var byId = report.Entries
                    .GroupBy(e => e.Id)
                    .ToDictionary(g => g.Key, g => g.ToList(), StringComparer.OrdinalIgnoreCase);

                checks.Add(("坏 JSON 判失败", State(byId, "<broken-json>").Any(s => s == ModuleState.Failed), Detail(byId, "<broken-json>")));
                checks.Add(("缺程序集判失败", State(byId, "pcl.fixture.missing").SequenceEqual(new[] { ModuleState.Failed }), Detail(byId, "pcl.fixture.missing")));
                checks.Add(("OnLoad 异常判失败", State(byId, "pcl.fixture.boom").SequenceEqual(new[] { ModuleState.Failed }), Detail(byId, "pcl.fixture.boom")));
                checks.Add(("依赖失败者判跳过", State(byId, "pcl.fixture.dependent").SequenceEqual(new[] { ModuleState.Skipped }), Detail(byId, "pcl.fixture.dependent")));
                checks.Add(("循环依赖判失败", State(byId, "pcl.fixture.cycle.a").Concat(State(byId, "pcl.fixture.cycle.b")).All(s => s == ModuleState.Failed), Detail(byId, "pcl.fixture.cycle.a")));
                checks.Add(("重复 Id 只留最高版本", State(byId, "pcl.fixture.dup").OrderBy(s => s).SequenceEqual(new[] { ModuleState.Started, ModuleState.Failed }), Detail(byId, "pcl.fixture.dup")));
                checks.Add(("正常模块应启动", State(byId, "pcl.fixture.good").SequenceEqual(new[] { ModuleState.Started }), Detail(byId, "pcl.fixture.good")));
                checks.Add(("整体判为不健康", !report.IsHealthy && report.ExitCode == 1, $"IsHealthy={report.IsHealthy} ExitCode={report.ExitCode}"));

                // 停止顺序：逆序调用，不能抛
                host.StopModules();
                checks.Add(("逆序停止不抛异常", report.Entries.All(e => e.State != ModuleState.Failed || e.Error != null), ""));
            }

            Console.WriteLine("== 故障注入用例 ==");
            Console.WriteLine();
            foreach (var (name, passed, detail) in checks)
                Console.WriteLine($"{(passed ? "PASS" : "FAIL")}  {name}{(string.IsNullOrEmpty(detail) ? "" : "  ← " + detail)}");

            var failed = checks.Count(c => !c.Passed);
            Console.WriteLine();
            Console.WriteLine($"{checks.Count - failed}/{checks.Count} 通过，夹具目录：{root}");
            try { Directory.Delete(root, true); } catch { /* 日志文件可能被占用，忽略 */ }
            return failed == 0 ? 0 : 1;
        }

        private static IEnumerable<ModuleState> State(Dictionary<string, List<ModuleEntry>> byId, string id)
            => byId.TryGetValue(id, out var list) ? list.Select(e => e.State) : Enumerable.Empty<ModuleState>();

        private static string Detail(Dictionary<string, List<ModuleEntry>> byId, string id)
            => byId.TryGetValue(id, out var list)
                ? string.Join(" | ", list.Select(e => $"{e.State}:{e.Error}"))
                : "未发现该模块";

        private static void WriteModule(string root, string folder, string content, string self)
        {
            var dir = Path.Combine(root, folder);
            Directory.CreateDirectory(dir);
            File.WriteAllText(Path.Combine(dir, "module.json"), content);
        }

        private static void WriteManifest(string root, string folder, string id, string version, string assembly,
            string entry, string self, string[] dependencies = null)
        {
            var dir = Path.Combine(root, folder);
            Directory.CreateDirectory(dir);
            var manifest = new
            {
                id,
                name = id,
                version,
                assembly,
                entry,
                dependencies = dependencies ?? new string[0],
                loadPriority = 0
            };
            File.WriteAllText(Path.Combine(dir, "module.json"),
                Newtonsoft.Json.JsonConvert.SerializeObject(manifest, Newtonsoft.Json.Formatting.Indented));
        }
    }

    /// <summary>故意在 OnLoad 里抛异常，用于验证加载器的异常隔离。</summary>
    public sealed class BoomModule : IPclModule
    {
        public void OnLoad(IModuleContext context) => throw new InvalidOperationException("故意炸的模块");
        public void OnStart() { }
        public void OnStop() { }
    }

    /// <summary>正常模块，用于对照。</summary>
    public sealed class GoodFixtureModule : IPclModule
    {
        public void OnLoad(IModuleContext context) => context.Log.Info("夹具模块加载");
        public void OnStart() { }
        public void OnStop() { }
    }
}
