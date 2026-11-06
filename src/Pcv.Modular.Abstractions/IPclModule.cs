using System;
using System.Collections.Generic;

namespace Pcv.Modular.Abstractions
{
    /// <summary>模块生命周期状态。状态只会单向推进，失败后不再继续。</summary>
    public enum ModuleState
    {
        Discovered = 0,
        Resolved = 1,
        Loaded = 2,
        Started = 3,
        Stopped = 4,
        /// <summary>依赖缺失或依赖失败，本模块被跳过。</summary>
        Skipped = 5,
        Failed = 6
    }

    /// <summary>
    /// 所有 PCL 模块的入口契约。实现类必须有无参构造函数。
    /// </summary>
    public interface IPclModule
    {
        /// <summary>程序集加载完成、服务尚未对外可用时调用。用于注册服务、读取配置。</summary>
        void OnLoad(IModuleContext context);

        /// <summary>所有模块 OnLoad 完成后按依赖顺序调用。此时可以安全调用其它模块的服务。</summary>
        void OnStart();

        /// <summary>按依赖逆序调用。用于释放线程、文件句柄。</summary>
        void OnStop();
    }

    /// <summary>模块可用的宿主上下文。</summary>
    public interface IModuleContext
    {
        ModuleManifest Manifest { get; }
        /// <summary>模块所在目录（含 module.json 与依赖 dll）。</summary>
        string ModuleDirectory { get; }

        /// <summary>
        /// 这个框架自己的数据目录，互不干扰（PCLData\frameworks\&lt;id&gt;）。
        /// 配置仍然走 ISettingsService 共享，文件各放各的——这就是"运行空间隔离、数据互通"的落点。
        /// </summary>
        string DataDirectory { get; }
        ILogger Log { get; }
        IServiceRegistry Services { get; }
        IEventBus Events { get; }
        HostInfo Host { get; }
        /// <summary>模块私有数据槽，不跨模块共享。</summary>
        IDictionary<string, object> Data { get; }
    }

    /// <summary>宿主环境信息。</summary>
    public sealed class HostInfo
    {
        public string Name { get; set; } = "PCL Modular Host";
        public string Version { get; set; } = "1.0.0";
        public string BaseDirectory { get; set; } = AppDomain.CurrentDomain.BaseDirectory;
        public string ModulesDirectory { get; set; }
    }

    public interface ILogger
    {
        void Debug(string message);
        void Info(string message);
        void Warn(string message);
        void Error(string message, Exception exception = null);
    }

    public interface IServiceRegistry
    {
        void Register<T>(T service) where T : class;
        void Register(Type contract, object service);
        T Get<T>() where T : class;
        bool TryGet<T>(out T service) where T : class;
        IReadOnlyDictionary<string, object> Snapshot();
    }

    public interface IEventBus
    {
        void Publish<TEvent>(TEvent payload);
        IDisposable Subscribe<TEvent>(Action<TEvent> handler);
    }
}
