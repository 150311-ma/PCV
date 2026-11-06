using System;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Pcv.Modular.Abstractions;
using Pcv.Modular.Contracts;

namespace Pcv.Launch.Microsoft
{
    /// <summary>
    /// 微软登录框架：装载时把上次的令牌读回来，界面上能重新登录。
    /// </summary>
    public sealed class MicrosoftLaunchModule : IPclModule, IFrameworkSurface
    {
        private ILogger _log;
        private ISettingsService _settings;
        private MsAuthService _service;

        public string Title => "微软登录";

        public void OnLoad(IModuleContext context)
        {
            _log = context.Log;

            ISettingsService settings = null;
            context.Services.TryGet(out settings);
            _settings = settings;

            _service = new MsAuthService(_log, _settings);
            context.Services.Register<IMicrosoftAuthService>(_service);

            var cached = _service.CachedAccount;
            _log.Info(cached == null
                ? "当前没有已登录的微软账号"
                : $"发现已保存的账号：{cached.Name}");
        }

        public void OnStart() { }

        public void OnStop()
        {
            _settings?.Save();
        }

        public object CreateView(IModuleContext context)
        {
            var root = new StackPanel();

            var status = new TextBlock
            {
                FontSize = 13,
                TextWrapping = TextWrapping.Wrap,
                Foreground = new SolidColorBrush(Color.FromRgb(0x34, 0x3D, 0x4A))
            };
            root.Children.Add(status);

            var hint = new TextBlock
            {
                FontSize = 12,
                TextWrapping = TextWrapping.Wrap,
                LineHeight = 20,
                Margin = new Thickness(0, 10, 0, 0),
                Foreground = new SolidColorBrush(Color.FromRgb(0x7C, 0x87, 0x98))
            };
            root.Children.Add(hint);

            var button = new Button
            {
                Content = "开始登录（设备码）",
                Width = 190,
                Height = 38,
                Margin = new Thickness(0, 18, 0, 0),
                HorizontalAlignment = HorizontalAlignment.Left,
                Cursor = System.Windows.Input.Cursors.Hand
            };
            root.Children.Add(button);

            void Refresh()
            {
                if (!_service.IsConfigured)
                {
                    status.Text = "没有配置 ClientId";
                    hint.Text = "开源版不带内置的。设置环境变量 PCL_MS_CLIENT_ID，或在配置里写 MsClientId，再打开这个界面。";
                    button.IsEnabled = false;
                    return;
                }

                var account = _service.CachedAccount;
                if (account == null)
                {
                    status.Text = "当前没有登录";
                    hint.Text = "点下面的按钮，浏览器里会给你一个代码，输进去就行。";
                }
                else
                {
                    status.Text = "已登录：" + account.Name;
                    hint.Text = account.ExpiresAt > System.DateTimeOffset.UtcNow
                        ? "令牌有效期到 " + account.ExpiresAt.LocalDateTime.ToString("yyyy-MM-dd HH:mm")
                        : "令牌已经过期了，重新登录一次。";
                }
                button.IsEnabled = true;
            }

            button.Click += async (sender, args) =>
            {
                button.IsEnabled = false;
                try
                {
                    status.Text = "正在申请设备码…";
                    var device = await _service.RequestDeviceCodeAsync(CancellationToken.None);
                    status.Text = "在浏览器打开 " + device.VerificationUri;
                    hint.Text = "代码：" + device.UserCode + "（" + device.ExpiresInSeconds + " 秒内有效）";

                    var progress = new Progress<string>(text => hint.Text = text);
                    var account = await _service.CompleteLoginAsync(device, progress, CancellationToken.None);

                    status.Text = "登录成功：" + account.Name;
                    hint.Text = "UUID " + account.Uuid;
                    context.Log.Info("界面里完成登录：" + account.Name);
                }
                catch (System.Exception ex)
                {
                    status.Text = "登录失败";
                    hint.Text = ex.Message;
                    context.Log.Error("界面里登录失败", ex);
                }
                finally
                {
                    Refresh();
                }
            };

            Refresh();
            return root;
        }
    }
}
