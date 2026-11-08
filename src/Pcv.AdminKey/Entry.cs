using System;
using System.IO;
using System.Net;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Newtonsoft.Json;
using Pcv.Modular.Abstractions;
using Pcv.Modular.Contracts;

namespace Pcv.AdminKey
{
    /// <summary>
    /// 在线凭据模块：每 10 分钟向服务端要一次当前凭证。
    ///
    /// 密钥由服务端按时间片算出来（HMAC），模块不自己生成、也不落盘，
    /// 所以「本地改代码伪造一个密钥」是行不通的。断网时保留上一次的
    /// 结果但标记 offline，壳会拒绝用离线密钥新开管理员模式。
    ///
    /// 定时器每 30 秒走一次：只是把倒计时减掉，到点了才真的发请求，
    /// 不会十分钟里一直打扰服务器。
    /// </summary>
    public sealed class Entry : IPclModule, IFrameworkSurface
    {
        private AdminKeyProvider _provider;

        public string Title => "在线凭据";

        public void OnLoad(IModuleContext context)
        {
            _provider = new AdminKeyProvider(context);
            context.Services.Register<IAdminKeyProvider>(_provider);
        }

        public void OnStart() => _provider?.Start();

        public void OnStop() => _provider?.Stop();

        public object CreateView(IModuleContext context)
            => _provider?.CreateView() ?? new TextBlock { Text = "密钥模块没起来" };
    }

    public sealed class AdminKeyProvider : IAdminKeyProvider
    {
        private const int TimeoutMs = 10000;
        private const int MaxAttempts = 3;
        private const int TickSeconds = 30;

        private readonly IModuleContext _context;
        private readonly ILogger _log;
        private Timer _timer;
        private string _site = "https://cyan-blog-2fk.pages.dev";
        private string _accessKey = "";
        private AdminKeyInfo _current = new AdminKeyInfo { Error = "还没连上服务器" };
        private int _refreshing;

        public AdminKeyProvider(IModuleContext context)
        {
            _context = context;
            _log = context.Log;
        }

        public AdminKeyInfo Current
        {
            get => _current;
            set { _current = value ?? new AdminKeyInfo(); KeyChanged?.Invoke(_current); }
        }

        public event Action<AdminKeyInfo> KeyChanged;

        public void SetAccessKey(string key)
        {
            _accessKey = (key ?? "").Trim();
            // 换凭证立刻重连一次，别等下个十分钟
            Task.Run(async () => { try { await RefreshAsync(); } catch { } });
        }

        public void Start()
        {
            TryBindGateway();
            _timer = new Timer(_ => Tick(), null, TimeSpan.Zero, TimeSpan.FromSeconds(TickSeconds));
        }

        public void Stop()
        {
            _timer?.Dispose();
            _timer = null;
        }

        private void TryBindGateway()
        {
            // OnStart 时所有模块的 OnLoad 都完成了，这会儿才敢去问网关要站点和密钥。
            // 站点与接入密钥的真相源是 IApiGateway，这里不另存一份常量，
            // 服务设置里改了接入密钥，事件一到就跟着变。
            _context.Services.TryGet(out IApiGateway gateway);
            if (gateway == null) return;
            _site = gateway.Site;
            _accessKey = gateway.AccessKey;
            gateway.Changed += _ =>
            {
                _site = gateway.Site;
                _accessKey = gateway.AccessKey;
            };
        }

        private void Tick()
        {
            var info = _current;
            if (info == null || !info.Online || info.ExpiresIn <= TickSeconds)
            {
                Task.Run(async () => { try { await RefreshAsync(); } catch { } });
                return;
            }
            info.ExpiresIn = Math.Max(0, info.ExpiresIn - TickSeconds);
            KeyChanged?.Invoke(info);
        }

        public bool Validate(string input)
        {
            var info = _current;
            if (info == null || !info.Online || string.IsNullOrWhiteSpace(info.Key)) return false;
            return string.Equals((input ?? "").Trim(), info.Key.Trim(), StringComparison.Ordinal);
        }

        public async Task<AdminKeyInfo> RefreshAsync()
        {
            if (Interlocked.Exchange(ref _refreshing, 1) == 1)
                return _current;
            try
            {
                var json = await Task.Run(() => Http(_site + "/pcv-api/admin-key", "GET", _accessKey, null))
                                     .ConfigureAwait(false);
                var info = JsonConvert.DeserializeObject<AdminKeyInfo>(json);
                if (info == null || string.IsNullOrWhiteSpace(info.Key))
                    throw new InvalidDataException("服务端没给凭证（正文可能被截断）");
                info.Online = true;
                info.Error = "";
                if (info.Interval <= 0) info.Interval = 600;
                Current = info;
                return info;
            }
            catch (Exception ex)
            {
                var failed = new AdminKeyInfo
                {
                    Key = "", Online = false, Interval = 600, ExpiresIn = 0,
                    Error = Short(ex.Message)
                };
                Current = failed;
                _log?.Warn("凭证刷新失败：" + failed.Error);
                return failed;
            }
            finally
            {
                Interlocked.Exchange(ref _refreshing, 0);
            }
        }

        // ── HTTP ───────────────────────────────────────────────────────────

        private static string Http(string url, string method, string key, string body)
        {
            Exception last = null;
            for (var attempt = 1; attempt <= MaxAttempts; attempt++)
            {
                try
                {
                    ServicePointManager.SecurityProtocol |= SecurityProtocolType.Tls12;
                    var request = (HttpWebRequest)WebRequest.Create(
                        url + (url.Contains("?") ? "&" : "?") + "n=" + attempt);
                    request.Method = method;
                    request.Timeout = TimeoutMs;
                    request.ReadWriteTimeout = TimeoutMs;
                    request.UserAgent = "PCV-AdminKey/1";
                    request.Headers["X-PCV-Api-Key"] = key ?? "";
                    request.Headers[HttpRequestHeader.CacheControl] = "no-cache";
                    using (var response = (HttpWebResponse)request.GetResponse())
                    using (var stream = response.GetResponseStream())
                    using (var reader = new StreamReader(stream, Encoding.UTF8))
                    {
                        var text = reader.ReadToEnd();
                        // 截断的 JSON 解析不了就重试，不把半截响应当成功
                        var probe = JsonConvert.DeserializeObject<AdminKeyInfo>(text);
                        if (probe == null) throw new InvalidDataException("响应不完整");
                        return text;
                    }
                }
                catch (Exception ex) { last = ex; }
            }
            throw last ?? new InvalidOperationException("请求失败");
        }

        private static string Short(string text)
        {
            if (string.IsNullOrEmpty(text)) return "原因不明";
            var one = text.Replace("\r", " ").Replace("\n", " ");
            return one.Length > 90 ? one.Substring(0, 90) + "…" : one;
        }

        // ── 模块自己的界面 ───────────────────────────────────────────────

        public object CreateView()
        {
            var root = new StackPanel { Margin = new Thickness(4) };
            root.Children.Add(new TextBlock
            {
                Text = "在线凭据", FontSize = 16, FontWeight = FontWeights.SemiBold,
                Foreground = new SolidColorBrush(Color.FromRgb(0x13, 0x1A, 0x24))
            });
            root.Children.Add(new TextBlock
            {
                Text = "每 10 分钟轮换一次。当前凭证由服务设置页显示，本地不生成、不保存。",
                FontSize = 11.5, TextWrapping = TextWrapping.Wrap,
                Foreground = new SolidColorBrush(Color.FromRgb(0x7A, 0x86, 0x99)),
                Margin = new Thickness(0, 6, 0, 16)
            });
            var keyText = new TextBlock
            {
                FontSize = 30, FontWeight = FontWeights.SemiBold,
                Foreground = new SolidColorBrush(Color.FromRgb(0x13, 0x70, 0xF3))
            };
            var state = new TextBlock
            {
                FontSize = 11.5, Margin = new Thickness(0, 8, 0, 0),
                Foreground = new SolidColorBrush(Color.FromRgb(0x7A, 0x86, 0x99))
            };
            root.Children.Add(keyText);
            root.Children.Add(state);
            Action paint = () =>
            {
                var info = _current;
                keyText.Text = info != null && info.Online ? info.Key : "— — — — — —";
                state.Text = info == null ? "还没连上"
                    : info.Online ? $"在线 · {info.ExpiresText} 后更换 · 服务器时间 {info.ServerTime}"
                    : "离线：" + info.Error;
            };
            paint();
            KeyChanged += _ =>
            {
                var dispatcher = Application.Current?.Dispatcher;
                if (dispatcher == null) return;
                if (dispatcher.CheckAccess()) paint(); else dispatcher.Invoke(paint);
            };
            return root;
        }
    }
}
