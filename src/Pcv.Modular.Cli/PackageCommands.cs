using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using Pcv.Modular.Host;
using Pcv.Modular.Packages;

namespace Pcv.Modular.Cli
{
    /// <summary>
    /// 功能框架包的命令行入口。
    /// 存在的意义：界面上拖一下是给人用的，这里是给"验证"用的 —— 同一套安装器，两条入口。
    /// </summary>
    internal static class PackageCommands
    {
        private static PackageInstaller CreateInstaller(string modulesDirectory, out ModuleLogger logger)
        {
            logger = new ModuleLogger("package", Path.Combine(modulesDirectory, "..", "logs", "packages.log"), false);
            return new PackageInstaller(modulesDirectory, logger);
        }

        public static int Install(string modulesDirectory, string packagePath, bool overwrite)
        {
            var installer = CreateInstaller(modulesDirectory, out _);
            var inspection = installer.Inspect(packagePath);

            Console.WriteLine($"包：{Path.GetFullPath(packagePath)}");
            Console.WriteLine($"sha256：{inspection.Sha256}");
            Console.WriteLine($"内容：{inspection.EntryCount} 个文件，{inspection.TotalBytes / 1024.0:0.#} KB");
            if (inspection.Manifest != null)
                Console.WriteLine($"清单：{inspection.Manifest.Id} {inspection.Manifest.Version}（依赖 {(inspection.Manifest.Dependencies.Count == 0 ? "无" : string.Join(", ", inspection.Manifest.Dependencies))}）");

            foreach (var warning in inspection.Warnings) Console.WriteLine("  提醒：" + warning);
            if (!inspection.IsValid)
            {
                Console.WriteLine();
                Console.WriteLine("拒绝安装：");
                foreach (var error in inspection.Errors) Console.WriteLine("  · " + error);
                return 2;
            }

            var result = installer.Install(packagePath, overwrite);
            Console.WriteLine();
            Console.WriteLine(result.Message);
            if (result.BackupPath != null && System.IO.Directory.Exists(result.BackupPath))
                Console.WriteLine("旧版本已备份到：" + result.BackupPath);
            return result.Success ? 0 : 3;
        }

        public static int List(string modulesDirectory)
        {
            var installer = CreateInstaller(modulesDirectory, out _);
            var modules = installer.ListInstalled();
            Console.WriteLine($"模块目录：{Path.GetFullPath(modulesDirectory)}");
            Console.WriteLine();
            if (modules.Count == 0)
            {
                Console.WriteLine("（没有安装任何框架）");
                return 0;
            }

            Console.WriteLine($"{"Id",-24} {"版本",-10} {"安装时间",-20} 来源");
            Console.WriteLine(new string('-', 78));
            foreach (var module in modules)
            {
                Console.WriteLine($"{module.Id,-24} {module.Version,-10} {module.InstalledAt:yyyy-MM-dd HH:mm:ss}   {module.SourceFile ?? "内置"}");
            }
            return 0;
        }

        public static int Uninstall(string modulesDirectory, string moduleId)
        {
            var installer = CreateInstaller(modulesDirectory, out _);
            var removed = installer.Uninstall(moduleId, out var message);
            Console.WriteLine(message);
            return removed ? 0 : 2;
        }

        /// <summary>
        /// 安装器的故障注入：造出各类坏包，确认安装器会按预期拒绝，而不是"装进去了才炸"。
        /// </summary>
        public static int SelfTest()
        {
            var root = Path.Combine(Path.GetTempPath(), "pcl-package-fixtures", Guid.NewGuid().ToString("N").Substring(0, 8));
            var modules = Path.Combine(root, "modules");
            Directory.CreateDirectory(modules);
            var self = typeof(PackageCommands).Assembly.Location;
            var checks = new List<(string Name, bool Passed, string Detail)>();

            // 1. 合法包
            var good = MakePackage(root, "good", "fixture.good", "1.0.0", new[] { "pcv.core" }, self, includeAssembly: true);
            // 2. 没有清单
            var noManifest = Path.Combine(root, "no-manifest.pclpkg");
            using (var zip = ZipFile.Open(noManifest, ZipArchiveMode.Create))
            {
                var entry = zip.CreateEntry("readme.txt");
                using (var writer = new StreamWriter(entry.Open())) writer.Write("no manifest here");
            }
            // 3. zip slip
            var slip = MakePackage(root, "slip", "fixture.slip", "1.0.0", Array.Empty<string>(), self, includeAssembly: true, extraEntry: "../evil.txt");
            // 4. 缺依赖
            var missingDep = MakePackage(root, "missing-dep", "fixture.dep", "1.0.0", new[] { "pcl.not.installed" }, self, includeAssembly: true);
            // 5. 清单声明的程序集不在包里
            var missingAssembly = MakePackage(root, "missing-asm", "fixture.asm", "1.0.0", Array.Empty<string>(), self, includeAssembly: false);

            var installer = new PackageInstaller(modules, new ModuleLogger("selftest", Path.Combine(root, "log.txt"), false));

            var inspectNoManifest = installer.Inspect(noManifest);
            checks.Add(("缺 module.json 被拒绝", inspectNoManifest.Errors.Any(e => e.Contains("没有 module.json")), string.Join(" | ", inspectNoManifest.Errors)));

            var inspectSlip = installer.Inspect(slip);
            checks.Add(("路径穿越被拒绝", inspectSlip.Errors.Any(e => e.Contains("非法路径")), string.Join(" | ", inspectSlip.Errors)));

            var inspectDep = installer.Inspect(missingDep);
            checks.Add(("缺前置框架被拒绝", inspectDep.Errors.Any(e => e.Contains("缺少前置框架")), string.Join(" | ", inspectDep.Errors)));

            var inspectAsm = installer.Inspect(missingAssembly);
            checks.Add(("清单程序集不在包内被拒绝", inspectAsm.Errors.Any(e => e.Contains("没有这个文件")), string.Join(" | ", inspectAsm.Errors)));

            // 为了让 good 能装上，先补一个 pcl.core 占位目录（依赖检查只看目录是否存在）
            Directory.CreateDirectory(Path.Combine(modules, "pcv.core"));
            File.WriteAllText(Path.Combine(modules, "pcv.core", "module.json"),
                "{\"id\":\"pcv.core\",\"name\":\"核心\",\"version\":\"1.0.0\",\"assembly\":\"x.dll\",\"dependencies\":[]}");

            var goodInstall = installer.Install(good);
            checks.Add(("合法包安装成功", goodInstall.Success, goodInstall.Message));
            checks.Add(("安装后目录存在", Directory.Exists(Path.Combine(modules, "fixture.good")), modules));
            checks.Add(("写入 install.json", File.Exists(Path.Combine(modules, "fixture.good", "install.json")), ""));
            checks.Add(("清掉 staging", !Directory.Exists(Path.Combine(modules, ".staging")) || !Directory.GetDirectories(Path.Combine(modules, ".staging")).Any(), ""));

            var again = installer.Install(good);
            checks.Add(("重复安装默认被拒", !again.Success && again.Message.Contains("已安装"), again.Message));

            var overwrite = installer.Install(good, true);
            checks.Add(("勾选覆盖后安装成功", overwrite.Success, overwrite.Message));
            checks.Add(("旧版本进了 .backup", overwrite.BackupPath != null && Directory.Exists(overwrite.BackupPath), overwrite.BackupPath ?? "null"));

            var uninstall = installer.Uninstall("fixture.good", out var uninstallMessage);
            checks.Add(("卸载移入 .trash", uninstall && !Directory.Exists(Path.Combine(modules, "fixture.good")), uninstallMessage));
            checks.Add(("基础框架不许卸载", !installer.Uninstall("pcv.core", out var coreMessage) && coreMessage.Contains("不能卸载"), coreMessage));

            Console.WriteLine("== 安装器故障注入 ==");
            Console.WriteLine();
            foreach (var (name, passed, detail) in checks)
                Console.WriteLine($"{(passed ? "PASS" : "FAIL")}  {name}{(string.IsNullOrEmpty(detail) ? "" : "  ← " + detail)}");

            var failed = checks.Count(c => !c.Passed);
            Console.WriteLine();
            Console.WriteLine($"{checks.Count - failed}/{checks.Count} 通过，夹具目录：{root}");
            try { Directory.Delete(root, true); } catch { }
            return failed == 0 ? 0 : 1;
        }

        private static string MakePackage(string root, string name, string id, string version,
            string[] dependencies, string assemblyPath, bool includeAssembly, string extraEntry = null)
        {
            var path = Path.Combine(root, name + ".pclpkg");
            var manifest = new StringBuilder();
            manifest.Append("{\n");
            manifest.Append($"  \"id\": \"{id}\",\n");
            manifest.Append($"  \"name\": \"{id}\",\n");
            manifest.Append($"  \"version\": \"{version}\",\n");
            manifest.Append($"  \"assembly\": \"{Path.GetFileName(assemblyPath)}\",\n");
            manifest.Append("  \"entry\": \"\",\n");
            manifest.Append($"  \"dependencies\": [{string.Join(", ", dependencies.Select(d => "\"" + d + "\""))}]\n");
            manifest.Append("}\n");

            using (var zip = ZipFile.Open(path, ZipArchiveMode.Create))
            {
                var entry = zip.CreateEntry("module.json");
                using (var writer = new StreamWriter(entry.Open(), new UTF8Encoding(false))) writer.Write(manifest.ToString());

                if (includeAssembly) zip.CreateEntryFromFile(assemblyPath, Path.GetFileName(assemblyPath));

                if (extraEntry != null)
                {
                    var evil = zip.CreateEntry(extraEntry);
                    using (var writer = new StreamWriter(evil.Open())) writer.Write("should never be extracted");
                }
            }
            return path;
        }
    }
}
