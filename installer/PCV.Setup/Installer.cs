using System;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Reflection;
using Microsoft.Win32;

namespace PCV.Setup
{
    public sealed class InstallResult
    {
        public bool Success { get; set; }
        public string Message { get; set; }
        public string Target { get; set; }
    }

    /// <summary>
    /// 安装逻辑：解包、建快捷方式、写卸载项。
    /// 全程不需要管理员权限 —— 默认装到 %LOCALAPPDATA%\Programs，和大多数现代应用一个路子。
    /// </summary>
    public static class Installer
    {
        public const string AppName = "PAINCHARNWAV";
        public const string Version = "1.0.0";
        public const string ExeName = "PCV.exe";

        public static string DefaultTarget => Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Programs", AppName);

        public static InstallResult Install(string target, bool shortcuts, Action<string> progress = null,
            bool writeRegistry = true)
        {
            try
            {
                progress?.Invoke("结束正在运行的 PCV…");
                StopRunning();

                progress?.Invoke("解压文件到 " + target);
                Directory.CreateDirectory(target);
                ExtractPayload(target);

                progress?.Invoke("布置卸载程序…");
                var self = Process.GetCurrentProcess().MainModule.FileName;
                var uninstall = Path.Combine(target, "Uninstall.exe");
                File.Copy(self, uninstall, true);

                if (shortcuts)
                {
                    progress?.Invoke("创建快捷方式…");
                    var exe = Path.Combine(target, ExeName);
                    CreateShortcut(Path.Combine(DesktopPath, AppName + ".lnk"), exe, target);
                    Directory.CreateDirectory(StartMenuFolder);
                    CreateShortcut(Path.Combine(StartMenuFolder, AppName + ".lnk"), exe, target);
                }

                if (writeRegistry)
                {
                    progress?.Invoke("登记卸载项…");
                    WriteRegistry(target);
                }

                progress?.Invoke("装好了");
                return new InstallResult
                {
                    Success = true,
                    Target = target,
                    Message = "已安装到 " + target
                };
            }
            catch (UnauthorizedAccessException)
            {
                return new InstallResult
                {
                    Success = false,
                    Target = target,
                    Message = "没有写入权限。换个目录试试（别选 Program Files，除非用管理员身份运行）。"
                };
            }
            catch (Exception ex)
            {
                return new InstallResult
                {
                    Success = false,
                    Target = target,
                    Message = "安装失败：" + ex.Message
                };
            }
        }

        private static void ExtractPayload(string target)
        {
            var assembly = Assembly.GetExecutingAssembly();
            using (var stream = assembly.GetManifestResourceStream("PCV.Setup.payload.zip"))
            {
                if (stream == null) throw new InvalidOperationException("安装包里没有 payload.zip（构建脚本漏了）");
                using (var archive = new ZipArchive(stream, ZipArchiveMode.Read))
                {
                    var root = Path.GetFullPath(target).TrimEnd('\\') + "\\";
                    foreach (var entry in archive.Entries)
                    {
                        var destination = Path.GetFullPath(Path.Combine(target, entry.FullName));
                        if (!destination.StartsWith(root, StringComparison.OrdinalIgnoreCase))
                            throw new InvalidOperationException("安装包里出现越界路径：" + entry.FullName);

                        if (string.IsNullOrEmpty(entry.Name))
                        {
                            Directory.CreateDirectory(destination);
                            continue;
                        }

                        Directory.CreateDirectory(Path.GetDirectoryName(destination));
                        entry.ExtractToFile(destination, true);
                    }
                }
            }
        }

        private static void StopRunning()
        {
            foreach (var process in Process.GetProcessesByName("PCV"))
            {
                try
                {
                    process.Kill();
                    process.WaitForExit(3000);
                }
                catch
                {
                    // 关不掉就算了，装完提示用户
                }
            }
        }

        public static string DesktopPath =>
            Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);

        public static string StartMenuFolder => Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.Programs), AppName);

        /// <summary>用 WScript.Shell 的后期绑定建快捷方式，省得为 COM 引一个互操作程序集。</summary>
        /// <summary>建快捷方式。安装流程和 --shortcut 参数都走这里，只有一份实现。</summary>
        public static void CreateShortcut(string linkPath, string targetExe, string workingDirectory)
        {
            var type = Type.GetTypeFromProgID("WScript.Shell");
            if (type == null) throw new InvalidOperationException("系统里没有 WScript.Shell，建不了快捷方式");

            dynamic shell = Activator.CreateInstance(type);
            dynamic link = shell.CreateShortcut(linkPath);
            link.TargetPath = targetExe;
            link.WorkingDirectory = workingDirectory;
            link.Description = AppName;
            link.IconLocation = targetExe + ",0";
            link.Save();
        }

        private static void WriteRegistry(string target)
        {
            using (var key = Registry.CurrentUser.CreateSubKey(
                @"Software\Microsoft\Windows\CurrentVersion\Uninstall\" + AppName))
            {
                if (key == null) return;
                key.SetValue("DisplayName", AppName);
                key.SetValue("DisplayVersion", Version);
                key.SetValue("Publisher", AppName);
                key.SetValue("InstallLocation", target);
                key.SetValue("DisplayIcon", Path.Combine(target, ExeName));
                key.SetValue("UninstallString", "\"" + Path.Combine(target, "Uninstall.exe") + "\" --uninstall");
                key.SetValue("NoModify", 1, RegistryValueKind.DWord);
                key.SetValue("NoRepair", 1, RegistryValueKind.DWord);

                try
                {
                    long size = 0;
                    foreach (var file in Directory.GetFiles(target, "*", SearchOption.AllDirectories))
                        size += new FileInfo(file).Length;
                    key.SetValue("EstimatedSize", (int)(size / 1024), RegistryValueKind.DWord);
                }
                catch
                {
                    // 算不出来就不写，不影响卸载
                }
            }
        }
    }

    /// <summary>卸载：删快捷方式、删注册表、删目录。目录最后删，因为正在里面跑。</summary>
    public static class Uninstaller
    {
        public static void Run()
        {
            var self = Process.GetCurrentProcess().MainModule.FileName;
            var target = Path.GetDirectoryName(self) ?? Installer.DefaultTarget;

            foreach (var process in Process.GetProcessesByName("PCV"))
            {
                try { process.Kill(); process.WaitForExit(3000); } catch { }
            }

            TryDelete(Path.Combine(Installer.DesktopPath, Installer.AppName + ".lnk"));
            TryDeleteDirectory(Installer.StartMenuFolder);

            try
            {
                Registry.CurrentUser.DeleteSubKeyTree(
                    @"Software\Microsoft\Windows\CurrentVersion\Uninstall\" + Installer.AppName, false);
            }
            catch
            {
                // 注册表项本来就不在
            }

            // 自己就在要删的目录里：交给 cmd，等两秒再删
            var command = "/c ping 127.0.0.1 -n 3 >nul & rmdir /s /q \"" + target + "\"";
            Process.Start(new ProcessStartInfo("cmd.exe", command)
            {
                CreateNoWindow = true,
                UseShellExecute = false
            });
        }

        private static void TryDelete(string path)
        {
            try { if (File.Exists(path)) File.Delete(path); } catch { }
        }

        private static void TryDeleteDirectory(string path)
        {
            try { if (Directory.Exists(path)) Directory.Delete(path, true); } catch { }
        }
    }
}
