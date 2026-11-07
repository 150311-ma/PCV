using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using Pcv.Modular.Abstractions;

namespace Pcv.Modular.Host
{
    public sealed class ModuleHostOptions
    {
        /// <summary>模块根目录，约定每个模块一个子目录。</summary>
        public string ModulesDirectory { get; set; }
        /// <summary>日志写入的文件；为空则只输出到控制台。</summary>
        public string LogFile { get; set; }
        public bool EchoToConsole { get; set; } = true;
        public string HostName { get; set; } = "PCV Modular Host";
        public string HostVersion { get; set; } = "2.10.0";
    }

    /// <summary>运行时的一个模块实例，记录状态、耗时与失败原因。</summary>
    public sealed class ModuleEntry
    {
        /// <summary>加载时创建的上下文。界面要打开框架时得把同一个上下文交给它。</summary>
        public IModuleContext Context { get; internal set; }

        public ModuleManifest Manifest { get; internal set; }
        public ModuleState State { get; internal set; } = ModuleState.Discovered;
        public string Error { get; internal set; }
        public Exception Exception { get; internal set; }
        public IPclModule Instance { get; internal set; }
        public Assembly Assembly { get; internal set; }
        public long LoadMilliseconds { get; internal set; }
        public long StartMilliseconds { get; internal set; }
        public string Id => Manifest?.Id ?? "<invalid>";

        internal void Fail(string message, Exception ex = null)
        {
            State = ModuleState.Failed;
            Error = message;
            Exception = ex;
        }

        internal void Skip(string message)
        {
            State = ModuleState.Skipped;
            Error = message;
        }

        public override string ToString() => $"{Id} [{State}]{(Error == null ? "" : " " + Error)}";
    }

    public sealed class ServiceRegistry : IServiceRegistry
    {
        private readonly Dictionary<string, object> _services = new Dictionary<string, object>(StringComparer.Ordinal);

        public void Register<T>(T service) where T : class => Register(typeof(T), service);

        public void Register(Type contract, object service)
        {
            if (contract == null) throw new ArgumentNullException(nameof(contract));
            if (service == null) throw new ArgumentNullException(nameof(service));
            if (!contract.IsInstanceOfType(service))
                throw new ArgumentException($"{service.GetType().FullName} 没有实现 {contract.FullName}");
            _services[contract.FullName] = service;
        }

        public T Get<T>() where T : class
        {
            if (_services.TryGetValue(typeof(T).FullName ?? typeof(T).Name, out var value)) return (T)value;
            throw new KeyNotFoundException($"服务 {typeof(T).FullName} 未注册（可能需要先加载提供它的模块）");
        }

        public bool TryGet<T>(out T service) where T : class
        {
            if (_services.TryGetValue(typeof(T).FullName ?? typeof(T).Name, out var value))
            {
                service = (T)value;
                return true;
            }
            service = null;
            return false;
        }

        public IReadOnlyDictionary<string, object> Snapshot() => _services
            .ToDictionary(p => p.Key, p => p.Value);
    }

    public sealed class EventBus : IEventBus
    {
        private readonly Dictionary<Type, List<Delegate>> _handlers = new Dictionary<Type, List<Delegate>>();
        private readonly object _lock = new object();

        public void Publish<TEvent>(TEvent payload)
        {
            Delegate[] snapshot;
            lock (_lock)
            {
                if (!_handlers.TryGetValue(typeof(TEvent), out var list)) return;
                snapshot = list.ToArray();
            }
            foreach (var handler in snapshot)
            {
                try
                {
                    ((Action<TEvent>)handler)(payload);
                }
                catch
                {
                    // 事件订阅者的异常不允许打断其它订阅者与加载流程
                }
            }
        }

        public IDisposable Subscribe<TEvent>(Action<TEvent> handler)
        {
            if (handler == null) throw new ArgumentNullException(nameof(handler));
            lock (_lock)
            {
                if (!_handlers.TryGetValue(typeof(TEvent), out var list))
                {
                    list = new List<Delegate>();
                    _handlers[typeof(TEvent)] = list;
                }
                list.Add(handler);
            }
            return new Subscription(() =>
            {
                lock (_lock)
                {
                    if (_handlers.TryGetValue(typeof(TEvent), out var list)) list.Remove(handler);
                }
            });
        }

        private sealed class Subscription : IDisposable
        {
            private Action _dispose;
            public Subscription(Action dispose) => _dispose = dispose;
            public void Dispose()
            {
                _dispose?.Invoke();
                _dispose = null;
            }
        }
    }

    public sealed class ModuleLogger : ILogger
    {
        private readonly string _scope;
        private readonly string _logFile;
        private readonly bool _echo;
        private readonly object _lock = new object();

        public ModuleLogger(string scope, string logFile, bool echo)
        {
            _scope = scope;
            _logFile = logFile;
            _echo = echo;
        }

        public void Debug(string message) => Write("DEBUG", message);
        public void Info(string message) => Write("INFO ", message);
        public void Warn(string message) => Write("WARN ", message);

        public void Error(string message, Exception exception = null)
            => Write("ERROR", exception == null ? message : message + " :: " + exception.GetType().Name + ": " + exception.Message);

        private void Write(string level, string message)
        {
            var line = $"[{DateTime.Now:HH:mm:ss.fff}] [{level}] [{_scope}] {message}";
            lock (_lock)
            {
                if (_echo) Console.WriteLine(line);
                if (string.IsNullOrEmpty(_logFile)) return;
                try
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(_logFile));
                    File.AppendAllText(_logFile, line + Environment.NewLine);
                }
                catch
                {
                    // 日志失败绝不允许影响主流程
                }
            }
        }
    }

    internal sealed class ModuleContext : IModuleContext
    {
        public ModuleContext(ModuleManifest manifest, string directory, ILogger log, IServiceRegistry services, IEventBus events, HostInfo host)
        {
            Manifest = manifest;
            ModuleDirectory = directory;
            Log = log;
            Services = services;
            Events = events;
            Host = host;

            // 每个框架一个自己的数据目录；建不出来就退回模块目录，不让它因此起不来
            try
            {
                var dataDirectory = Path.Combine(host.BaseDirectory, "PCLData", "frameworks", manifest.Id);
                Directory.CreateDirectory(dataDirectory);
                DataDirectory = dataDirectory;
            }
            catch
            {
                DataDirectory = directory;
            }
        }

        public ModuleManifest Manifest { get; }
        public string ModuleDirectory { get; }
        public string DataDirectory { get; }
        public ILogger Log { get; }
        public IServiceRegistry Services { get; }
        public IEventBus Events { get; }
        public HostInfo Host { get; }
        public IDictionary<string, object> Data { get; } = new Dictionary<string, object>(StringComparer.Ordinal);
    }
}
