using System;
using System.Collections.Generic;
using System.IO;
using System.Windows;
using Newtonsoft.Json;
using Pcv.Modular.Abstractions;
using Pcv.Modular.Host;
using Pcv.Modular.Runtime;

namespace Pcv.Shell
{
    /// <summary>
    /// 壳里的"框架运行入口"。
    ///
    /// 两种框架走同一个口子出去：
    ///   · 程序集型（.NET DLL）→ 由 ModuleHost 在进程内加载，窗口是 FrameworkWindow；
    ///   · 进程型（py / node / exe）→ 由 ProcessFramework 起子进程，窗口是 ProcessWindow。
    /// 进程型不经过 ModuleHost，因为它压根不是 .NET 程序集。
    /// </summary>
    public sealed class ModuleHostAccessor
    {
        private readonly ModuleHost _host;
        private readonly Dictionary<string, ProcessWindow> _windows =
            new Dictionary<string, ProcessWindow>(StringComparer.OrdinalIgnoreCase);

        public ModuleHostAccessor(ModuleHost host)
        {
            _host = host;
            BaseDirectory = AppDomain.CurrentDomain.BaseDirectory;
        }

        public string BaseDirectory { get; }
        public string ModulesDirectory => _host.ModulesDirectory;
        public ILogger Log => _host.Log;

        public static ModuleManifest ReadManifest(string directory)
        {
            try
            {
                var path = Path.Combine(directory, "module.json");
                if (!File.Exists(path)) return null;
                var manifest = JsonConvert.DeserializeObject<ModuleManifest>(File.ReadAllText(path));
                if (manifest != null) manifest.DirectoryPath = directory;
                return manifest;
            }
            catch
            {
                return null;
            }
        }

        public bool IsRunning(string moduleId) => FrameworkRegistry.Find(moduleId) != null;

        /// <summary>
        /// 把某个包依赖里"还没跑起来的进程型框架"启动起来（不开窗口，纯后台）。
        /// 已经在跑的不重复启动 —— 两个软件共用一个框架时不会打出两份。
        /// </summary>
        public void StartDependencies(ModuleManifest manifest)
        {
            foreach (var dependencyId in manifest.Dependencies ?? new System.Collections.Generic.List<string>())
            {
                if (FrameworkRegistry.Find(dependencyId) != null) continue;

                var directory = Path.Combine(ModulesDirectory, dependencyId);
                var dependency = ReadManifest(directory);
                if (dependency == null) continue;
                if (!string.Equals(dependency.Kind, "process", StringComparison.OrdinalIgnoreCase)) continue;

                try
                {
                    var dataDirectory = Path.Combine(BaseDirectory, "PCLData", "frameworks", dependencyId);
                    Directory.CreateDirectory(dataDirectory);

                    var framework = new ProcessFramework(dependency, Log, dataDirectory);
                    framework.Start();
                    Log.Info($"随 {manifest.Id} 自动启动框架 {dependencyId}（不开窗口）");
                }
                catch (Exception ex)
                {
                    Log.Error($"自动启动依赖 {dependencyId} 失败", ex);
                }
            }
        }

        /// <summary>启动一个进程型框架并给它一个窗口；已经在跑就把窗口拉到前面。</summary>
        public void RunProcessFramework(ModuleManifest manifest, string directory, Window owner)
        {
            if (manifest == null) throw new ArgumentNullException(nameof(manifest));

            if (_windows.TryGetValue(manifest.Id, out var existing) && existing.IsVisible)
            {
                existing.Activate();
                return;
            }

            // 软件是"调用框架干活"的，那它依赖的框架得先活着。
            // 这里只拉进程型依赖：程序集型框架早被 ModuleHost 加载过了。
            StartDependencies(manifest);

            var dataDirectory = Path.Combine(BaseDirectory, "PCLData", "frameworks", manifest.Id);
            Directory.CreateDirectory(dataDirectory);

            var framework = new ProcessFramework(manifest, Log, dataDirectory);
            framework.Start();
            Log.Info($"[proc] {manifest.Id} 进程已启动，准备开窗口");

            var window = new ProcessWindow(framework);
            Log.Info("[proc] 窗口对象建好了");
            if (owner != null && owner.IsVisible && !ReferenceEquals(owner, window)) window.Owner = owner;

            _windows[manifest.Id] = window;
            window.Closed += (s, e) => _windows.Remove(manifest.Id);
            window.Show();
            Log.Info($"[proc] 窗口已显示（Visible={window.IsVisible}）");
        }
    }
}
