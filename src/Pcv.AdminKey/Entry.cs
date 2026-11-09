using System;
using System.IO;
using System.Net;
using System.Text;
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
    /// 凭证校验模块：把用户在桌面端输入的 6 位数交给服务端判，客户端不持有答案。
    ///
    /// 为什么是「送校验」而不是「取凭证」：
    ///   以前这个模块是向服务端要当前凭证，再在本地拿用户输入去比对。
    ///   那有两处硬伤 ——
    ///     1. 要凭证就得先有「接入密钥」，而那个密钥必须分发给客户端才用得上，
    ///        一进客户端就等于公开（它以前明文写在开源仓库里，还内嵌进了 exe）；
    ///     2. 本地比对意味着客户端知道正确答案，「输入凭证」只是表演。
    ///
    ///   现在改成：凭证只在博客后台（管理员会话）显示，用户抄下来手输，
    ///   客户端把它 POST 到 /pcv-api/verify，服务端判对错。
    ///   客户端一行秘密都不留，断网就解不开 —— 这是有意的代价。
    ///
    /// 服务端对这个接口有失败限流（同 IP 连错 8 次锁 10 分钟），
    /// 因为 6 位数字只有 100 万种，不限流是能被枚举的。
    /// </summary>
    public sealed class Entry : IPclModule, IFrameworkSurface
    {
        private AdminKeyProvider _provider;

        public string Title => "凭证校验";

        public void OnLoad(IModuleContext context)
        {
            _provider = new AdminKeyProvider(context);
            context.Services.Register<IAdminKeyProvider>(_provider);
        }

        public void OnStart() { /* 不再定时拉取：只在用户提交时校验 */ }

        public void OnStop() { }

        public object CreateView(IModuleContext context)
            => _provider?.CreateView() ?? new TextBlock { Text = "凭证模块没起来" };
    }

    public sealed class AdminKeyProvider : IAdminKeyProvider
    {
        private const int TimeoutMs = 10000;
        private const int MaxAttempts = 2;

        private readonly IModuleContext _context;
        private readonly ILogger _log;
        private string _site = "https://cyan-blog-2fk.pages.dev";
        private AdminKeyInfo _current = new AdminKeyInfo { Error = "还没校验过" };

        public AdminKeyProvider(IModuleContext context)
        {
            _context = context;
            _log = context.Log;
            TryBindSite();
        }

        public AdminKeyInfo Current
        {
            get => _current;
            set { _current = value ?? new AdminKeyInfo(); KeyChanged?.Invoke(_current); }
        }

        public event Action<AdminKeyInfo> KeyChanged;

        private void TryBindSite()
        {
            // 站点地址从网关取，别在模块里另存一份常量
            if (_context.Services.TryGet(out IApiGateway gateway)
                && gateway != null && !string.IsNullOrWhiteSpace(gateway.Site))
            {
                _site = gateway.Site.TrimEnd('/');
            }
        }

        /// <summary>
        /// 把用户输入的凭证交给服务端判。
        /// 返回的 AdminKeyInfo 只带「通过没有 / 还有多久换」，**不含凭证本身**。
        /// </summary>
        public async Task<AdminKeyInfo> VerifyAsync(string input)
        {
            TryBindSite();

            var digits = OnlyDigits(input);
            if (digits.Length != 6)
            {
                var bad = new AdminKeyInfo
                {
                    Verified = false, Online = true,
                    Error = "凭证是 6 位数字，请核对网页后台显示的那一串",
                };
                Current = bad;
                return bad;
            }

            try
            {
                var payload = JsonConvert.SerializeObject(new { credential = digits });
                var (status, text) = await Task.Run(() => Http(_site + "/pcv-api/verify", "POST", payload))
                                              .ConfigureAwait(false);

                var doc = JsonConvert.DeserializeObject<VerifyResponse>(text);

                if (status == 429)
                {
                    var limited = new AdminKeyInfo
                    {
                        Verified = false, Online = true,
                        Error = doc != null && !string.IsNullOrWhiteSpace(doc.Error)
                            ? doc.Error : "试错太多，等一会儿再试",
                    };
                    Current = limited;
                    return limited;
                }

                if (doc == null || !doc.Ok)
                {
                    var denied = new AdminKeyInfo
                    {
                        Verified = false, Online = true,
                        Error = doc != null && !string.IsNullOrWhiteSpace(doc.Error)
                            ? doc.Error : "凭证不对",
                    };
                    Current = denied;
                    return denied;
                }

                var ok = new AdminKeyInfo
                {
                    Verified = true,
                    Online = true,
                    Interval = doc.Interval > 0 ? doc.Interval : 600,
                    ExpiresIn = doc.ExpiresIn,
                    ServerTime = doc.ServerTime,
                    Error = "",
                };
                Current = ok;
                _log?.Info("维护模式凭证校验通过");
                return ok;
            }
            catch (Exception ex)
            {
                var failed = new AdminKeyInfo
                {
                    Verified = false, Online = false,
                    Error = "连不上服务器：" + Short(ex.Message),
                };
                Current = failed;
                _log?.Warn("凭证校验失败：" + failed.Error);
                return failed;
            }
        }

        private static string OnlyDigits(string text)
        {
            if (string.IsNullOrWhiteSpace(text)) return "";
            var builder = new StringBuilder(6);
            foreach (var ch in text)
            {
                if (ch >= '0' && ch <= '9') builder.Append(ch);
                if (builder.Length == 6) break;
            }
            return builder.ToString();
        }

        // ── HTTP ───────────────────────────────────────────────────────────

        private static (int Status, string Body) Http(string url, string method, string body)
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
                    request.UserAgent = "PCV-AdminKey/2";
                    request.ContentType = "application/json";
                    request.Accept = "application/json";
                    request.Headers[HttpRequestHeader.CacheControl] = "no-cache";

                    if (!string.IsNullOrEmpty(body))
                    {
                        var bytes = Encoding.UTF8.GetBytes(body);
                        request.ContentLength = bytes.Length;
                        using (var stream = request.GetRequestStream())
                            stream.Write(bytes, 0, bytes.Length);
                    }

                    using (var response = (HttpWebResponse)request.GetResponse())
                    using (var stream = response.GetResponseStream())
                    using (var reader = new StreamReader(stream, Encoding.UTF8))
                        return ((int)response.StatusCode, reader.ReadToEnd());
                }
                catch (WebException ex) when (ex.Response is HttpWebResponse error)
                {
                    // 401 / 429 是「有意义的回答」，不是网络故障 —— 直接读正文返回
                    var response = error;
                    using (var stream = response.GetResponseStream())
                    using (var reader = new StreamReader(stream, Encoding.UTF8))
                        return ((int)response.StatusCode, reader.ReadToEnd());
                }
                catch (Exception ex) { last = ex; }
            }
            throw last ?? new InvalidOperationException("请求失败");
        }

        private sealed class VerifyResponse
        {
            [JsonProperty("ok")] public bool Ok { get; set; }
            [JsonProperty("error")] public string Error { get; set; }
            [JsonProperty("interval")] public int Interval { get; set; }
            [JsonProperty("expiresIn")] public int ExpiresIn { get; set; }
            [JsonProperty("serverTime")] public string ServerTime { get; set; }
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
                Text = "凭证校验", FontSize = 16, FontWeight = FontWeights.SemiBold,
                Foreground = new SolidColorBrush(Color.FromRgb(0x13, 0x1A, 0x24))
            });
            root.Children.Add(new TextBlock
            {
                Text = "凭证只在博客后台显示（需要管理员账号）。桌面端不保存凭证，"
                     + "只在解锁时把它交给服务端判一次；断网时无法解锁。",
                FontSize = 11.5, TextWrapping = TextWrapping.Wrap,
                Foreground = new SolidColorBrush(Color.FromRgb(0x7A, 0x86, 0x99)),
                Margin = new Thickness(0, 6, 0, 16)
            });

            var input = new PasswordBox { Height = 34, MaxLength = 16, Padding = new Thickness(9, 0, 9, 0) };
            var button = new Button
            {
                Content = "校验", Height = 34, Width = 92,
                Margin = new Thickness(10, 0, 0, 0),
            };
            var state = new TextBlock
            {
                FontSize = 11.5, Margin = new Thickness(0, 12, 0, 0),
                TextWrapping = TextWrapping.Wrap,
                Foreground = new SolidColorBrush(Color.FromRgb(0x7A, 0x86, 0x99))
            };

            var row = new StackPanel { Orientation = Orientation.Horizontal };
            row.Children.Add(input);
            row.Children.Add(button);
            root.Children.Add(row);
            root.Children.Add(state);

            button.Click += async (s, e) =>
            {
                button.IsEnabled = false;
                state.Text = "正在问服务器…";
                var info = await VerifyAsync(input.Password).ConfigureAwait(true);
                button.IsEnabled = true;
                state.Text = info.Verified
                    ? "通过 · 有效 " + info.ExpiresText
                    : (info.Error ?? "没通过");
            };
            return root;
        }
    }
}
