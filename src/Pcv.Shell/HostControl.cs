using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Windows;
using Pcv.Modular.Contracts;
using Pcv.Modular.Host;
using Pcv.Modular.Packages;

namespace Pcv.Shell
{
    /// <summary>
    /// IHostControl 的宿主侧实现。
    ///
    /// 权限边界（这是"给框架更高权限"这句话的全部含义）：
    ///   · 框架能读到的是 HostModuleInfo 这种纯数据快照，不是 ModuleEntry，更不是窗口；
    ///   · 框架能调用的是这里列出的九个动作，每个都由宿主自己执行；
    ///   · 装包仍然走 PackageInstaller 的完整校验，卸载仍然挡住基础模块；
    ///   · 宿主没准备好时（窗口还没建出来）所有动作安全返回 false，不抛异常到框架里。
    ///
    /// 这样框架从"只能被调用"变成"能请求宿主做事"，但拿不到 UI 树的任何引用。
    /// </summary>
    public sealed class HostControl : IHostControl
    {
        private readonly ModuleHost _host;
        private MainWindow _window;

        public HostControl(ModuleHost host)
        {
            _host = host ?? throw new ArgumentNullException(nameof(host));
        }

        /// <summary>窗口建好之后由 App 调一次；注册服务发生在窗口之前，所以这里是延迟绑定。</summary>
        public void Attach(MainWindow window)
        {
            _window = window;
            Raise("ready");
        }

        public string HostVersion => _host.Host.Version;
        public string BaseDirectory => _host.Host.BaseDirectory;

        public IReadOnlyList<HostModuleInfo> Modules
        {
            get
            {
                var list = new List<HostModuleInfo>();
                try
                {
                    var installer = new PackageInstaller(_host.ModulesDirectory, _host.Log);
                    foreach (var installed in installer.ListInstalled())
                    {
                        var manifest = ModuleHostAccessor.ReadManifest(installed.Directory);
                        var entry = _window?.CurrentReport?.Entries
                            .FirstOrDefault(e => string.Equals(e.Id, installed.Id, StringComparison.OrdinalIgnoreCase));
                        list.Add(new HostModuleInfo
                        {
                            Id = installed.Id,
                            Name = manifest?.DisplayName ?? installed.Name,
                            Version = manifest?.Version ?? installed.Version,
                            Category = manifest?.CategoryLabel ?? "框架",
                            State = entry != null ? entry.State.ToString() : "未加载",
                            Description = manifest?.Description ?? "",
                            Icon = IconOf(manifest?.Icon, manifest?.DisplayName ?? installed.Id),
                            Accent = manifest?.Accent ?? "",
                            CanOpen = manifest == null || !manifest.IsBaseFramework
                        });
                    }
                }
                catch (Exception ex)
                {
                    _host.Log.Warn("IHostControl.Modules 读取失败：" + ex.Message);
                }
                return list;
            }
        }

        private static string IconOf(string icon, string name)
        {
            if (!string.IsNullOrWhiteSpace(icon)) return icon.Trim();
            return string.IsNullOrEmpty(name) ? "P" : name.Substring(0, 1);
        }

        public bool Navigate(string view)
        {
            if (_window == null || string.IsNullOrWhiteSpace(view)) return false;
            // 维护页（api）**故意不在这张白名单里**：
            // 它是靠连点版本号进的，模块不该有办法把用户直接推过去 ——
            // 否则任何一个第三方模块都能绕开「藏起来」这件事。
            var allowed = new[] { "home", "install", "frameworks", "market", "credits", "onboarding" };
            if (Array.IndexOf(allowed, view) < 0) return false;
            try
            {
                _window.ShowViewForRender(view);
                _window.BringToFront();
                Raise("view");
                return true;
            }
            catch (Exception ex)
            {
                _host.Log.Warn("IHostControl.Navigate 失败：" + ex.Message);
                return false;
            }
        }

        public bool OpenModule(string moduleId)
        {
            if (_window == null || string.IsNullOrWhiteSpace(moduleId)) return false;
            try { _window.OpenModuleById(moduleId); return true; }
            catch (Exception ex) { _host.Log.Warn("IHostControl.OpenModule 失败：" + ex.Message); return false; }
        }

        public bool RefreshModules()
        {
            if (_window == null) return false;
            try { _window.RefreshFromHost(); Raise("modules"); return true; }
            catch (Exception ex) { _host.Log.Warn("IHostControl.RefreshModules 失败：" + ex.Message); return false; }
        }

        public bool InstallPackage(string packagePath, bool overwrite)
        {
            if (string.IsNullOrWhiteSpace(packagePath) || !File.Exists(packagePath)) return false;
            try
            {
                var installer = new PackageInstaller(_host.ModulesDirectory, _host.Log);
                var inspection = installer.Inspect(packagePath);
                if (!inspection.IsValid) { _host.Log.Warn("IHostControl 拒绝安装：" + inspection.Summary()); return false; }
                var result = installer.Install(packagePath, overwrite);
                if (result.Success) { _window?.RefreshFromHost(); Raise("modules"); }
                return result.Success;
            }
            catch (Exception ex) { _host.Log.Warn("IHostControl.InstallPackage 失败：" + ex.Message); return false; }
        }

        public bool UninstallModule(string moduleId)
        {
            if (string.IsNullOrWhiteSpace(moduleId)) return false;
            // 基础模块是壳跑起来的前提，谁都不能卸
            if (string.Equals(moduleId, "pcv.core", StringComparison.OrdinalIgnoreCase)) return false;
            try
            {
                var installer = new PackageInstaller(_host.ModulesDirectory, _host.Log);
                var ok = installer.Uninstall(moduleId, out _);
                if (ok) { _window?.RefreshFromHost(); Raise("modules"); }
                return ok;
            }
            catch (Exception ex) { _host.Log.Warn("IHostControl.UninstallModule 失败：" + ex.Message); return false; }
        }

        public void Notify(string message, bool isError = false)
        {
            try { _window?.SetHostStatus(message, isError); }
            catch (Exception ex) { _host.Log.Warn("IHostControl.Notify 失败：" + ex.Message); }
        }

        public bool CopyText(string text)
        {
            if (text == null) return false;
            try { return ClipboardWriter.TrySetText(text); }
            catch { return false; }
        }

        public bool OpenUrl(string url)
        {
            if (string.IsNullOrWhiteSpace(url)) return false;
            try
            {
                Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
                return true;
            }
            catch { return false; }
        }

        public event Action<string> HostChanged;

        private void Raise(string kind)
        {
            var handler = HostChanged;
            if (handler == null) return;
            try { handler(kind); }
            catch (Exception ex) { _host.Log.Warn("HostChanged 订阅者抛异常：" + ex.Message); }
        }
    }
}
