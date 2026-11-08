using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Text;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Newtonsoft.Json;
using Pcv.Modular.Abstractions;
using Pcv.Modular.Contracts;

namespace Pcv.Api
{
    /// <summary>
    /// API 接口模块：把「服务端有哪些接口、用什么密钥、能不能用」收在一个地方。
    ///
    /// 已有能力（比如博客原有的激活码验证）直接登记进清单就能用；
    /// 以后加在线功能只要往清单里加一条，壳和其它模块都只认 IApiGateway，
    /// 不用各自再写一遍 HTTP —— 这就是「新的通过 API 来接入」。
    ///
    /// 离线兜底用程序集里内嵌的 interfaces.json（由 tools/build_pcv_api.py
    /// 生成，和线上那份同一份字节），不在这里手抄第二份清单。
    /// </summary>
    public sealed class Entry : IPclModule, IFrameworkSurface
    {
        private ApiGateway _gateway;

        public string Title => "API 接口";

        public void OnLoad(IModuleContext context)
        {
            _gateway = new ApiGateway(context);
            context.Services.Register<IApiGateway>(_gateway);
        }

        public void OnStart() => _gateway?.Start();

        public void OnStop() { }

        public object CreateView(IModuleContext context)
        {
            if (_gateway == null) return new TextBlock { Text = "API 网关没起来" };
            return _gateway.CreateView();
        }
    }

    public sealed class ApiGateway : IApiGateway
    {
        private const string EmbeddedInterfaces = "Pcv.Api.interfaces.json";
        private const int TimeoutMs = 10000;
        private const int MaxAttempts = 3;

        private readonly IModuleContext _context;
        private readonly ISettingsService _settings;
        private readonly string _configFile;
        private readonly HashSet<string> _disabled = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        private readonly List<ApiEndpointInfo> _endpoints = new List<ApiEndpointInfo>();
        private string _site = "https://cyan-blog-2fk.pages.dev";
        private string _accessKey = "";
        private string _lastError = "";

        public ApiGateway(IModuleContext context)
        {
            _context = context;
            _configFile = Path.Combine(context.DataDirectory, "interfaces.json");
            context.Services.TryGet(out _settings);
            LoadLocal();
        }

        public string Site => _site;
        public string AccessKey => _accessKey;
        public string LastError => _lastError;
        public IReadOnlyList<ApiEndpointInfo> Endpoints => _endpoints.AsReadOnly();

        public event Action<string> Changed;

        // ── 配置 ────────────────────────────────────────────────────────────

        private void LoadLocal()
        {
            // 1) 内嵌兜底；2) 上次拉下来的本地副本（更新过的优先）
            Apply(ReadEmbedded());
            try
            {
                if (File.Exists(_configFile))
                    Apply(File.ReadAllText(_configFile, Encoding.UTF8));
            }
            catch { /* 本地副本坏了就用内嵌那份 */ }

            var configured = _settings?.GetValue("ApiAccessKey", "");
            if (!string.IsNullOrWhiteSpace(configured)) _accessKey = configured.Trim();
            var ids = _settings?.GetValue("ApiDisabledIds", "") ?? "";
            foreach (var id in ids.Split(new[] { ',' }, StringSplitOptions.RemoveEmptyEntries))
                _disabled.Add(id.Trim());
            foreach (var ep in _endpoints) ep.Enabled = !_disabled.Contains(ep.Id);
        }

        private static string ReadEmbedded()
        {
            var assembly = typeof(ApiGateway).Assembly;
            using (var stream = assembly.GetManifestResourceStream(EmbeddedInterfaces))
            {
                if (stream == null) return null;
                using (var reader = new StreamReader(stream, Encoding.UTF8))
                    return reader.ReadToEnd();
            }
        }

        private sealed class InterfacesDocument
        {
            public string Site { get; set; }
            public string AccessKey { get; set; }
            public List<ApiEndpointInfo> Endpoints { get; set; }
        }

        /// <summary>只认站内绝对路径；拦住「清单被改成外部 URL」这种事。</summary>
        private static bool SafePath(string path)
        {
            return !string.IsNullOrWhiteSpace(path) && path.StartsWith("/")
                   && path.IndexOf("://", StringComparison.Ordinal) < 0
                   && path.IndexOf("..", StringComparison.Ordinal) < 0;
        }

        private void Apply(string json)
        {
            if (string.IsNullOrWhiteSpace(json)) return;
            var doc = JsonConvert.DeserializeObject<InterfacesDocument>(json);
            if (doc == null || doc.Endpoints == null || doc.Endpoints.Count == 0) return;
            if (!string.IsNullOrWhiteSpace(doc.Site) && doc.Site.StartsWith("https://")) _site = doc.Site.TrimEnd('/');
            // 用户手改过的接入密钥优先，别被服务端默认值盖掉
            if (string.IsNullOrWhiteSpace(_accessKey) && !string.IsNullOrWhiteSpace(doc.AccessKey))
                _accessKey = doc.AccessKey.Trim();

            _endpoints.Clear();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var ep in doc.Endpoints)
            {
                if (ep == null || string.IsNullOrWhiteSpace(ep.Id) || !SafePath(ep.Path)) continue;
                if (!seen.Add(ep.Id)) continue;
                if (ep.Method != "GET" && ep.Method != "POST") ep.Method = "GET";
                ep.Enabled = !_disabled.Contains(ep.Id);
                _endpoints.Add(ep);
            }
        }

        private void SaveLocal(string json)
        {
            try
            {
                var folder = Path.GetDirectoryName(_configFile);
                if (!string.IsNullOrEmpty(folder)) Directory.CreateDirectory(folder);
                File.WriteAllText(_configFile, json, new UTF8Encoding(false));
            }
            catch { /* 写不上不影响这次使用，下次再拉 */ }
        }

        public void SetAccessKey(string key)
        {
            _accessKey = (key ?? "").Trim();
            _settings?.SetValue("ApiAccessKey", _accessKey);
            _settings?.Save();
            Changed?.Invoke("key");
        }

        public void SetEnabled(string id, bool enabled)
        {
            var ep = _endpoints.FirstOrDefault(e => string.Equals(e.Id, id, StringComparison.OrdinalIgnoreCase));
            if (ep == null) return;
            ep.Enabled = enabled;
            if (enabled) _disabled.Remove(ep.Id); else _disabled.Add(ep.Id);
            _settings?.SetValue("ApiDisabledIds", string.Join(",", _disabled));
            _settings?.Save();
            Changed?.Invoke("enabled");
        }

        public void Start()
        {
            // 启动后台拉一次；失败也不挡界面，内嵌清单照样能看
            Task.Run(async () => { try { await RefreshAsync(); } catch { } });
        }

        public async Task<bool> RefreshAsync()
        {
            try
            {
                var json = await Task.Run(() => Http(
                    _site + "/pcv-api/interfaces.json", "GET", null, null)).ConfigureAwait(false);
                var probe = JsonConvert.DeserializeObject<InterfacesDocument>(json);
                if (probe == null || probe.Endpoints == null || probe.Endpoints.Count == 0)
                    throw new InvalidDataException("接口清单是空的（可能被截断）");
                Apply(json);
                SaveLocal(json);
                _lastError = "";
                Changed?.Invoke("refresh");
                return true;
            }
            catch (Exception ex)
            {
                _lastError = Short(ex.Message);
                Changed?.Invoke("refresh-failed");
                return false;
            }
        }

        public async Task<ApiCallResult> CallAsync(string id, string body = null)
        {
            var ep = _endpoints.FirstOrDefault(e => string.Equals(e.Id, id, StringComparison.OrdinalIgnoreCase));
            if (ep == null) return new ApiCallResult { Ok = false, Message = "没有这个接口 id" };
            if (!ep.Enabled) return new ApiCallResult { Ok = false, Message = "这个接口已停用" };
            if (!SafePath(ep.Path)) return new ApiCallResult { Ok = false, Message = "接口路径不合法" };

            try
            {
                return await Task.Run(() =>
                {
                    var key = ep.NeedsKey ? _accessKey : null;
                    var (status, text) = HttpWithStatus(_site + ep.Path, ep.Method, key, body);
                    return new ApiCallResult
                    {
                        Ok = status >= 200 && status < 300,
                        Status = status,
                        Body = text,
                        Message = status >= 200 && status < 300 ? "通了" : "HTTP " + status
                    };
                }).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                return new ApiCallResult { Ok = false, Message = Short(ex.Message) };
            }
        }

        // ── HTTP（只用 BCL；CF 那段会偶发截断，重试到能解析为止）────────

        private static (int, string) HttpWithStatus(string url, string method, string key, string body)
        {
            Exception last = null;
            for (var attempt = 1; attempt <= MaxAttempts; attempt++)
            {
                try
                {
                    var request = Make(url + (url.Contains("?") ? "&" : "?") + "n=" + attempt, method, key);
                    if (method == "POST" && body != null)
                    {
                        var bytes = Encoding.UTF8.GetBytes(body);
                        request.ContentType = "text/plain; charset=utf-8";
                        request.ContentLength = bytes.Length;
                        using (var stream = request.GetRequestStream()) stream.Write(bytes, 0, bytes.Length);
                    }
                    using (var response = (HttpWebResponse)request.GetResponse())
                    using (var stream = response.GetResponseStream())
                    using (var reader = new StreamReader(stream, Encoding.UTF8))
                        return ((int)response.StatusCode, reader.ReadToEnd());
                }
                catch (WebException ex) when (ex.Response is HttpWebResponse err)
                {
                    using (var stream = err.GetResponseStream())
                    using (var reader = new StreamReader(stream, Encoding.UTF8))
                        return ((int)err.StatusCode, reader.ReadToEnd());
                }
                catch (Exception ex) { last = ex; }
            }
            throw last ?? new InvalidOperationException("请求失败");
        }

        private static string Http(string url, string method, string key, string body)
        {
            var (status, text) = HttpWithStatus(url, method, key, body);
            if (status < 200 || status >= 300) throw new InvalidDataException("HTTP " + status);
            // 截断的 JSON 解析不了就当失败，让上层重试
            JsonConvert.DeserializeObject<InterfacesDocument>(text);
            return text;
        }

        private static HttpWebRequest Make(string url, string method, string key)
        {
            ServicePointManager.SecurityProtocol |= SecurityProtocolType.Tls12;
            var request = (HttpWebRequest)WebRequest.Create(url);
            request.Method = method;
            request.Timeout = TimeoutMs;
            request.ReadWriteTimeout = TimeoutMs;
            request.UserAgent = "PCV-Api/1";
            request.Headers[HttpRequestHeader.CacheControl] = "no-cache";
            if (!string.IsNullOrEmpty(key)) request.Headers["X-PCV-Api-Key"] = key;
            return request;
        }

        private static string Short(string text)
        {
            if (string.IsNullOrEmpty(text)) return "原因不明";
            var one = text.Replace("\r", " ").Replace("\n", " ");
            return one.Length > 90 ? one.Substring(0, 90) + "…" : one;
        }

        // ── 模块自己的界面（框架也要有界面）────────────────────────────

        public object CreateView()
        {
            var root = new StackPanel { Margin = new Thickness(4) };
            root.Children.Add(new TextBlock
            {
                Text = "API 接口", FontSize = 16, FontWeight = FontWeights.SemiBold,
                Foreground = new SolidColorBrush(Color.FromRgb(0x13, 0x1A, 0x24))
            });
            var hint = new TextBlock
            {
                FontSize = 11.5, TextWrapping = TextWrapping.Wrap,
                Foreground = new SolidColorBrush(Color.FromRgb(0x7A, 0x86, 0x99)),
                Margin = new Thickness(0, 6, 0, 12)
            };
            root.Children.Add(hint);

            var list = new StackPanel();
            root.Children.Add(list);
            Action paint = () =>
            {
                list.Children.Clear();
                hint.Text = $"服务端 {_site} · {_endpoints.Count} 个接口"
                            + (string.IsNullOrEmpty(_lastError) ? "" : " · 上次刷新失败：" + _lastError);
                foreach (var ep in _endpoints)
                {
                    var line = new TextBlock
                    {
                        Text = (ep.Enabled ? "● " : "○ ") + ep.Name + "   " + ep.Method + " " + ep.Path,
                        FontSize = 11.5, Margin = new Thickness(0, 3, 0, 3),
                        Foreground = new SolidColorBrush(ep.Enabled
                            ? Color.FromRgb(0x1B, 0x6B, 0x45) : Color.FromRgb(0x9A, 0xA4, 0xB2))
                    };
                    list.Children.Add(line);
                }
            };
            paint();
            Changed += _ =>
            {
                var dispatcher = Application.Current?.Dispatcher;
                if (dispatcher == null) return;
                if (dispatcher.CheckAccess()) paint(); else dispatcher.Invoke(paint);
            };
            return root;
        }
    }
}
