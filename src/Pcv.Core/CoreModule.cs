using System;
using Pcv.Modular.Abstractions;
using Pcv.Modular.Contracts;

namespace Pcv.Core
{
    /// <summary>
    /// 基础模块：路径约定 + 配置持久化。
    ///
    /// 只做这两件事。主题、登录、清理都是别的模块的事——
    /// 这里多塞一行，以后每个模块都会想往这儿塞一行。
    /// </summary>
    public sealed class CoreModule : IPclModule
    {
        private ILogger _log;
        private JsonSettings _settings;
        private PathLayout _paths;

        public void OnLoad(IModuleContext context)
        {
            _log = context.Log;

            _paths = new PathLayout(context.Host.BaseDirectory);
            var failed = _paths.Ensure();
            if (failed.Length > 0)
                _log.Warn("目录创建失败，配置可能存不下来：" + string.Join("；", failed));

            _settings = new JsonSettings(_paths.SettingsFile);

            // 配置读坏了不算致命：空配置 + 一条警告，剩下的交给用户
            if (_settings.LastError != null)
                _log.Warn("配置读取有异常，已回退为空配置：" + _settings.LastError);

            context.Services.Register<ISettingsService>(_settings);
            context.Data["paths"] = _paths;

            _log.Info($"基础模块已装载：数据 {_paths.DataDirectory}，配置 {_paths.SettingsFile}");
        }

        public void OnStart()
        {
            _log.Info("基础模块就绪");
        }

        public void OnStop()
        {
            if (_settings == null || !_settings.HasUnsavedChanges) return;

            if (_settings.Save())
                _log.Info("配置已落盘");
            else
                _log.Error("配置落盘失败：" + _settings.LastError);
        }
    }
}
