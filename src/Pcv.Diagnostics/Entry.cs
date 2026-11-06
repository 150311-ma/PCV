using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using Pcv.Modular.Abstractions;
using Pcv.Modular.Contracts;

namespace Pcv.Diagnostics
{
    /// <summary>
    /// 环境自检。跑一遍，把结果写成一份报告丢在 logs\ 里。
    /// 出问题的时候，用户只要把这份文件发过来，就不用再问"你什么系统、装了几个包、盘还剩多少"。
    /// </summary>
    public sealed partial class Entry : IPclModule
    {
        private IModuleContext _context;

        public void OnLoad(IModuleContext context)
        {
            _context = context;
            context.Log.Info("环境自检已装载，启动时跑一次");
        }

        public void OnStart()
        {
            try
            {
                RunAndSave(out var file);
                _context.Log.Info("自检报告：" + file);
            }
            catch (Exception ex)
            {
                _context.Log.Error("自检没跑成", ex);
            }
        }

        /// <summary>跑一遍自检并写文件，返回报告正文与路径。界面上的"重新自检"也走这里。</summary>
        internal string RunAndSave(out string file)
        {
            var report = Collect();
            var folder = Path.Combine(_context.Host.BaseDirectory, "logs");
            Directory.CreateDirectory(folder);
            file = Path.Combine(folder, $"diagnostics-{DateTime.Now:yyyyMMdd-HHmmss}.txt");
            File.WriteAllText(file, report, Encoding.UTF8);
            return report;
        }

        public void OnStop()
        {
        }

        internal string Collect()
        {
            var lines = new List<string>
            {
                $"时间　　{DateTime.Now:yyyy-MM-dd HH:mm:ss}",
                $"宿主　　{_context.Host.Name} {_context.Host.Version}",
                $"程序目录　{_context.Host.BaseDirectory}",
                $"模块目录　{_context.Host.ModulesDirectory}",
                $"模块本体　{_context.ModuleDirectory}",
                "",
                "── 系统 ──",
                $"系统　　　{OsName()}",
                $"CLR 版本　{Environment.Version}",
                $"位数　　　{(Environment.Is64BitProcess ? "64 位" : "32 位")}",
                $"处理器数　{Environment.ProcessorCount}",
                $"用户　　　{Environment.UserName}",
                "",
                "── 磁盘 ──"
            };

            try
            {
                var root = Path.GetPathRoot(_context.Host.BaseDirectory);
                var drive = new DriveInfo(root);
                lines.Add($"盘符　　　{drive.Name}");
                lines.Add($"剩余空间　{drive.AvailableFreeSpace / 1024.0 / 1024 / 1024:0.0} GB / {drive.TotalSize / 1024.0 / 1024 / 1024:0.0} GB");
            }
            catch (Exception ex)
            {
                lines.Add("读磁盘信息失败：" + ex.Message);
            }

            lines.Add("");
            lines.Add("── 目录 ──");
            lines.Add("模块目录可写　" + (CanWrite(_context.Host.ModulesDirectory) ? "是" : "否（装包会失败）"));

            var modules = SafeDirectories(_context.Host.ModulesDirectory)
                .Where(d => !Path.GetFileName(d).StartsWith("."))
                .Select(Path.GetFileName)
                .OrderBy(n => n, StringComparer.OrdinalIgnoreCase)
                .ToList();
            lines.Add($"已装模块　　{modules.Count} 个：{string.Join("、", modules)}");

            var leftovers = SafeDirectories(_context.Host.ModulesDirectory)
                .Count(d => Path.GetFileName(d).StartsWith("."));
            lines.Add($"未清理目录　{leftovers} 个（.backup / .trash / .staging）");

            lines.Add("");
            lines.Add("── 设置 ──");
            ISettingsService settings = null;
            if (_context.Services.TryGet(out settings))
                lines.Add($"配置项　　　{settings.Keys.Count} 条");
            else
                lines.Add("配置服务不可用（pcl.core 没起来？）");

            return string.Join(Environment.NewLine, lines);
        }

        /// <summary>
        /// 取系统名字。直接读 Environment.OSVersion 的话，Win11 会被报成 "6.2.9200"（Win8），
        /// 因为进程没有声明支持的 OS 版本。注册表里的是真话。
        /// </summary>
        private static string OsName()
        {
            try
            {
                using (var key = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(
                    @"SOFTWARE\Microsoft\Windows NT\CurrentVersion"))
                {
                    if (key != null)
                    {
                        var name = key.GetValue("ProductName") as string;
                        var display = key.GetValue("DisplayVersion") as string;
                        var build = key.GetValue("CurrentBuild") as string;
                        if (!string.IsNullOrEmpty(name))
                        {
                            // Win11 的注册表里 ProductName 仍写着 "Windows 10"，只有 build 号能分辨
                            if (int.TryParse(build, out var buildNumber) && buildNumber >= 22000)
                                name = name.Replace("Windows 10", "Windows 11");
                            return $"{name} {display} (build {build})".Replace("  ", " ").Trim();
                        }
                    }
                }
            }
            catch
            {
                // 读不到就退回老办法
            }
            return Environment.OSVersion.ToString();
        }

        private static bool CanWrite(string directory)
        {
            try
            {
                Directory.CreateDirectory(directory);
                var probe = Path.Combine(directory, ".write-probe");
                File.WriteAllText(probe, "ok");
                File.Delete(probe);
                return true;
            }
            catch
            {
                return false;
            }
        }

        private static IEnumerable<string> SafeDirectories(string directory)
        {
            try
            {
                return Directory.Exists(directory) ? Directory.GetDirectories(directory) : Array.Empty<string>();
            }
            catch
            {
                return Array.Empty<string>();
            }
        }
    }
}
