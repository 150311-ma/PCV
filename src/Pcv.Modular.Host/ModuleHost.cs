using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using Newtonsoft.Json;
using Pcv.Modular.Abstractions;

namespace Pcv.Modular.Host
{
    /// <summary>
    /// PCV 模块加载器。
    ///
    /// 职责边界（刻意做窄）：
    ///   1. 发现   —— 扫目录、读 module.json，不猜、不扫程序集特性；
    ///   2. 解析   —— 依赖拓扑排序，循环依赖直接判失败，不“尽力而为”；
    ///   3. 加载   —— 反射创建入口类型，逐个 OnLoad，异常隔离；
    ///   4. 启动   —— 全部加载完成后按序 OnStart，失败不回滚已启动的模块但会记录；
    ///   5. 停止   —— 逆序 OnStop。
    ///
    /// 任何一步失败都只影响该模块及其下游，其它模块继续。
    /// </summary>
    public sealed class ModuleHost : IDisposable
    {
        private readonly ModuleHostOptions _options;
        private readonly ModuleLogger _log;
        private readonly List<ModuleEntry> _entries = new List<ModuleEntry>();
        private readonly HostInfo _hostInfo;
        private bool _disposed;

        public ModuleHost(ModuleHostOptions options = null)
        {
            _options = options ?? new ModuleHostOptions();
            if (string.IsNullOrWhiteSpace(_options.ModulesDirectory))
                _options.ModulesDirectory = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "modules");
            _options.ModulesDirectory = Path.GetFullPath(_options.ModulesDirectory);

            if (string.IsNullOrWhiteSpace(_options.LogFile))
                _options.LogFile = Path.Combine(_options.ModulesDirectory, "..", "logs", "modules.log");
            _options.LogFile = Path.GetFullPath(_options.LogFile);

            ModuleManifest.HostInfoVersion = _options.HostVersion;
            _hostInfo = new HostInfo
            {
                Name = _options.HostName,
                Version = _options.HostVersion,
                BaseDirectory = AppDomain.CurrentDomain.BaseDirectory,
                ModulesDirectory = _options.ModulesDirectory
            };

            _log = new ModuleLogger("host", _options.LogFile, _options.EchoToConsole);
            Services = new ServiceRegistry();
            Events = new EventBus();
            AppDomain.CurrentDomain.AssemblyResolve += ResolveModuleDependency;
        }

        public HostInfo Host => _hostInfo;
        public IServiceRegistry Services { get; }
        public IEventBus Events { get; }
        public ILogger Log => _log;
        public string ModulesDirectory => _options.ModulesDirectory;
        public IReadOnlyList<ModuleEntry> Modules => _entries;

        /// <summary>按“发现 → 解析 → 加载 → 启动”完整跑一遍。</summary>
        public ModuleLoadReport Run()
        {
            Discover();
            Resolve();
            LoadModules();
            StartModules();
            return BuildReport();
        }

        // ── 1. 发现 ────────────────────────────────────────────────────────────

        public void Discover()
        {
            _entries.Clear();
            if (!Directory.Exists(_options.ModulesDirectory))
            {
                _log.Warn($"模块目录不存在：{_options.ModulesDirectory}");
                return;
            }

            var manifestFiles = new List<string>();
            var rootManifest = Path.Combine(_options.ModulesDirectory, "module.json");
            if (File.Exists(rootManifest)) manifestFiles.Add(rootManifest);
            foreach (var dir in Directory.GetDirectories(_options.ModulesDirectory))
            {
                var file = Path.Combine(dir, "module.json");
                if (File.Exists(file)) manifestFiles.Add(file);
            }

            var candidates = new List<ModuleEntry>();
            foreach (var file in manifestFiles)
            {
                var entry = new ModuleEntry { Manifest = new ModuleManifest() };
                try
                {
                    var manifest = JsonConvert.DeserializeObject<ModuleManifest>(File.ReadAllText(file));
                    if (manifest == null) throw new InvalidDataException("module.json 内容为空或不是对象");
                    manifest.DirectoryPath = Path.GetDirectoryName(file);
                    entry.Manifest = manifest;

                    // 进程型框架不是程序集，加载器不管它；它由 Pcv.Modular.Runtime 起进程
                    if (string.Equals(manifest.Kind, "process", StringComparison.OrdinalIgnoreCase))
                    {
                        _log.Info($"{manifest.Id} 是进程型框架，交给运行时管理，不参与程序集加载");
                        continue;
                    }

                    var errors = manifest.Validate();
                    if (errors.Count > 0)
                    {
                        entry.Fail("清单无效：" + string.Join("；", errors));
                    }
                    candidates.Add(entry);
                }
                catch (Exception ex)
                {
                    entry.Manifest.Id = "<" + Path.GetFileName(Path.GetDirectoryName(file)) + ">";
                    entry.Fail("module.json 解析失败：" + ex.Message, ex);
                    candidates.Add(entry);
                }
            }

            // 同一 Id 出现多份时只保留版本最高的一份，其余显式记失败，避免“悄悄生效”的歧义
            var brokenAtParse = candidates.Where(c => c.State == ModuleState.Failed).ToList();
            foreach (var group in candidates.Where(c => c.State != ModuleState.Failed).GroupBy(c => c.Id))
            {
                var ordered = group
                    .OrderByDescending(c => ParseVersion(c.Manifest.Version))
                    .ToList();
                _entries.Add(ordered[0]);
                foreach (var loser in ordered.Skip(1))
                {
                    loser.Fail($"重复的模块 Id，已被 {ordered[0].Manifest.DirectoryPath} 覆盖");
                    _entries.Add(loser);
                }
            }
            _entries.AddRange(brokenAtParse);

            foreach (var entry in _entries.Where(e => e.State != ModuleState.Failed))
                entry.State = ModuleState.Resolved;

            _log.Info($"发现 {_entries.Count} 个模块（{_entries.Count(e => e.State != ModuleState.Failed)} 个有效）");
        }

        // ── 2. 解析（拓扑排序）──────────────────────────────────────────────────

        public void Resolve()
        {
            // 发现阶段直接判死的（坏 JSON、重复 Id）必须原样带到结果里，不能被拓扑排序吞掉
            var failedDuringDiscovery = _entries.Where(e => e.State == ModuleState.Failed).ToList();

            var byId = _entries
                .Where(e => e.State != ModuleState.Failed)
                .GroupBy(e => e.Id)
                .ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);

            // 依赖缺失
            foreach (var entry in byId.Values.ToList())
            {
                foreach (var dep in entry.Manifest.Dependencies ?? new List<string>())
                {
                    if (!byId.ContainsKey(dep))
                        entry.Fail($"缺少依赖模块：{dep}");
                }
            }

            var alive = byId.Values.Where(e => e.State != ModuleState.Failed).ToList();
            var indegree = alive.ToDictionary(e => e.Id, e => 0, StringComparer.OrdinalIgnoreCase);
            var dependents = alive.ToDictionary(e => e.Id, e => new List<ModuleEntry>(), StringComparer.OrdinalIgnoreCase);

            foreach (var entry in alive)
            {
                foreach (var dep in entry.Manifest.Dependencies ?? new List<string>())
                {
                    if (!indegree.ContainsKey(dep)) continue;
                    indegree[entry.Id]++;
                    dependents[dep].Add(entry);
                }
            }

            // 就绪队列按 LoadPriority 再按 Id 排序，保证每次加载顺序可复现
            var ready = new List<ModuleEntry>(alive.Where(e => indegree[e.Id] == 0));
            var ordered = new List<ModuleEntry>();
            while (ready.Count > 0)
            {
                ready.Sort(ComparePriority);
                var current = ready[0];
                ready.RemoveAt(0);
                ordered.Add(current);
                foreach (var dependent in dependents[current.Id])
                {
                    if (--indegree[dependent.Id] == 0) ready.Add(dependent);
                }
            }

            // 还有入度的就是环
            var stuck = alive.Except(ordered).ToList();
            if (stuck.Count > 0)
            {
                var cycleText = string.Join(" ↔ ", stuck.Select(e => e.Id));
                foreach (var entry in stuck) entry.Fail($"检测到循环依赖：{cycleText}");
                _log.Error($"循环依赖：{cycleText}");
            }

            _entries.Clear();
            _entries.AddRange(ordered);
            _entries.AddRange(stuck);
            _entries.AddRange(byId.Values.Where(e => e.State == ModuleState.Failed));
            _entries.AddRange(failedDuringDiscovery);
            _log.Info($"依赖解析完成，可加载 {ordered.Count} 个模块");
        }

        private static int ComparePriority(ModuleEntry a, ModuleEntry b)
        {
            var byPriority = a.Manifest.LoadPriority.CompareTo(b.Manifest.LoadPriority);
            return byPriority != 0 ? byPriority : string.CompareOrdinal(a.Id, b.Id);
        }

        // ── 3. 加载 ────────────────────────────────────────────────────────────

        public void LoadModules()
        {
            foreach (var entry in _entries)
            {
                if (entry.State == ModuleState.Failed) continue;

                var missingDependency = FindUnavailableDependency(entry);
                if (missingDependency != null)
                {
                    entry.Skip($"依赖 {missingDependency} 未成功加载");
                    _log.Warn($"{entry.Id} 跳过：依赖 {missingDependency} 未成功加载");
                    continue;
                }

                var watch = Stopwatch.StartNew();
                try
                {
                    var assemblyPath = Path.Combine(entry.Manifest.DirectoryPath, entry.Manifest.Assembly);
                    if (!File.Exists(assemblyPath))
                        throw new FileNotFoundException($"找不到程序集：{entry.Manifest.Assembly}", assemblyPath);

                    var assembly = Assembly.LoadFrom(assemblyPath);
                    entry.Assembly = assembly;

                    var moduleType = ResolveEntryType(assembly, entry.Manifest);
                    if (moduleType == null)
                        throw new TypeLoadException($"程序集 {entry.Manifest.Assembly} 中找不到 IPclModule 实现");

                    var instance = (IPclModule)Activator.CreateInstance(moduleType);
                    entry.Instance = instance;

                    var context = new ModuleContext(
                        entry.Manifest,
                        entry.Manifest.DirectoryPath,
                        new ModuleLogger(entry.Id, _options.LogFile, _options.EchoToConsole),
                        Services,
                        Events,
                        _hostInfo);

                    entry.Context = context;
                    instance.OnLoad(context);
                    entry.State = ModuleState.Loaded;
                    _log.Info($"已加载 {entry.Id}@{entry.Manifest.Version}（{watch.ElapsedMilliseconds} ms）");
                }
                catch (Exception ex)
                {
                    entry.Fail(Describe(ex), ex);
                    _log.Error($"加载 {entry.Id} 失败：{Describe(ex)}", ex);
                }
                finally
                {
                    entry.LoadMilliseconds = watch.ElapsedMilliseconds;
                }
            }
        }

        /// <summary>返回第一个未成功加载的依赖 Id；没有则返回 null。</summary>
        private string FindUnavailableDependency(ModuleEntry entry)
        {
            foreach (var dependencyId in entry.Manifest.Dependencies ?? new List<string>())
            {
                var dependencyEntry = _entries.FirstOrDefault(e => string.Equals(e.Id, dependencyId, StringComparison.OrdinalIgnoreCase));
                if (dependencyEntry == null) return dependencyId;
                if (dependencyEntry.State != ModuleState.Loaded && dependencyEntry.State != ModuleState.Started)
                    return dependencyId;
            }
            return null;
        }

        private static Type ResolveEntryType(Assembly assembly, ModuleManifest manifest)        {
            if (!string.IsNullOrWhiteSpace(manifest.Entry))
            {
                var type = assembly.GetType(manifest.Entry, throwOnError: false);
                if (type != null && typeof(IPclModule).IsAssignableFrom(type)) return type;
            }

            Type[] types;
            try
            {
                types = assembly.GetTypes();
            }
            catch (ReflectionTypeLoadException ex)
            {
                types = ex.Types.Where(t => t != null).ToArray();
            }
            return types.FirstOrDefault(t => typeof(IPclModule).IsAssignableFrom(t) && !t.IsAbstract && !t.IsInterface);
        }

        private static string Describe(Exception ex)
        {
            while (ex is TargetInvocationException && ex.InnerException != null) ex = ex.InnerException;
            switch (ex)
            {
                case FileNotFoundException fnf: return "缺少依赖文件：" + fnf.FileName;
                case BadImageFormatException _: return "程序集格式不正确（位数或目标框架不匹配）";
                case FileLoadException _: return "程序集加载被拒绝：" + ex.Message;
                case TypeLoadException _: return ex.Message;
                default: return ex.GetType().Name + ": " + ex.Message;
            }
        }

        // ── 4. 启动 ────────────────────────────────────────────────────────────

        public void StartModules()
        {
            foreach (var entry in _entries.Where(e => e.State == ModuleState.Loaded).ToList())
            {
                var watch = Stopwatch.StartNew();
                try
                {
                    entry.Instance.OnStart();
                    entry.State = ModuleState.Started;
                }
                catch (Exception ex)
                {
                    entry.Fail("OnStart 抛出异常：" + Describe(ex), ex);
                    _log.Error($"{entry.Id} 启动失败：{Describe(ex)}", ex);
                }
                finally
                {
                    entry.StartMilliseconds = watch.ElapsedMilliseconds;
                }
            }
        }

        // ── 5. 停止 ────────────────────────────────────────────────────────────

        public void StopModules()
        {
            foreach (var entry in _entries.Where(e => e.State == ModuleState.Started).Reverse().ToList())
            {
                try
                {
                    entry.Instance.OnStop();
                    entry.State = ModuleState.Stopped;
                }
                catch (Exception ex)
                {
                    entry.Fail("OnStop 抛出异常：" + Describe(ex), ex);
                    _log.Error($"{entry.Id} 停止失败：{Describe(ex)}", ex);
                }
            }
        }

        public ModuleLoadReport BuildReport() => new ModuleLoadReport(_entries, _options.ModulesDirectory, _hostInfo);

        /// <summary>
        /// 停掉所有已启动模块，清空模块表，重新走一遍"发现 → 解析 → 加载 → 启动"。
        /// 安装或卸载框架后用它刷新。
        ///
        /// 注意：.NET Framework 里程序集一旦加载就卸不掉，所以"卸载"不会让已加载的类型消失，
        /// 只有重启进程才彻底干净 —— 这一点在卸载提示里如实告诉用户。
        /// </summary>
        public ModuleLoadReport ReloadAll()
        {
            _log.Info("重新加载模块表");
            StopModules();
            _entries.Clear();
            return Run();
        }

        /// <summary>加载结束时按模块目录里的私有 dll 解析依赖，避免每个模块都要往宿主根丢文件。</summary>
        private Assembly ResolveModuleDependency(object sender, ResolveEventArgs args)
        {
            var simpleName = new AssemblyName(args.Name).Name;
            foreach (var entry in _entries)
            {
                if (entry.Manifest?.DirectoryPath == null) continue;
                var candidate = Path.Combine(entry.Manifest.DirectoryPath, simpleName + ".dll");
                if (File.Exists(candidate)) return Assembly.LoadFrom(candidate);
            }
            return null;
        }

        private static Version ParseVersion(string text)
        {
            if (!Version.TryParse((text ?? "0.0.0").Split('-')[0], out var version)) return new Version(0, 0, 0);
            return version;
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            StopModules();
            AppDomain.CurrentDomain.AssemblyResolve -= ResolveModuleDependency;
        }
    }
}
