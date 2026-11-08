using System;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Text;
using System.Threading.Tasks;
using Newtonsoft.Json;
using Pcv.Modular.Contracts;

namespace Pcv.Shell
{
    /// <summary>服务设置看到的市场状态，纯数据，不含 UI 类型。</summary>
    public sealed class MarketStatusInfo
    {
        public bool Enabled { get; set; }
        public bool Online { get; set; }
        public string Origin { get; set; }
        public int Items { get; set; }
        public string Updated { get; set; }
        public string Message { get; set; }
        public bool ServerAvailable { get; set; }
        public bool ServerMarketEnabled { get; set; }
        public int ServerItems { get; set; }
        public string Notice { get; set; }
    }

    /// <summary>自动更新的检查结果。两个通道都问，取更新的那个。</summary>
    public sealed class UpdateInfo
    {
        public string CurrentVersion { get; set; }
        public string ServerVersion { get; set; }
        public string GitHubVersion { get; set; }
        public string LatestVersion { get; set; }
        public bool HasUpdate { get; set; }
        public string ReleaseUrl { get; set; }
        public string Message { get; set; }
    }

    /// <summary>
    /// 服务设置背后的状态与动作：市场开关、状态聚合、缓存、公告。
    /// 市场的开关不写在服务端，而是本地设置 —— 关掉后，即使线上还开着，
    /// 这台机器也不会再去拉清单、也不会显示市场页内容。
    /// </summary>
    public sealed class AdminService
    {
        public const string MarketEnabledKey = "MarketEnabled";
        private const string DefaultSite = "https://cyan-blog-2fk.pages.dev";

        private readonly MarketService _market;
        private readonly ISettingsService _settings;
        private readonly string _site;

        public AdminService(MarketService market, ISettingsService settings, IApiGateway gateway)
        {
            _market = market;
            _settings = settings;
            _site = gateway != null && !string.IsNullOrWhiteSpace(gateway.Site)
                ? gateway.Site.TrimEnd('/') : DefaultSite;
            MarketEnabled = settings?.GetValue(MarketEnabledKey, true) ?? true;
            _market.Enabled = MarketEnabled;
        }

        public bool MarketEnabled { get; private set; }

        public void SetMarketEnabled(bool enabled)
        {
            MarketEnabled = enabled;
            _market.Enabled = enabled;
            _settings?.SetValue(MarketEnabledKey, enabled);
            _settings?.Save();
        }

        public MarketStatusInfo LocalStatus()
        {
            var last = _market.LastResult;
            var catalog = last?.Catalog;
            return new MarketStatusInfo
            {
                Enabled = MarketEnabled,
                Online = last != null && last.Success && last.Origin == "线上市场",
                Origin = last?.Origin ?? "还没读过",
                Items = catalog?.Items?.Count ?? 0,
                Updated = catalog?.Updated ?? "—",
                Message = last?.Message ?? "",
                Notice = "",
            };
        }

        /// <summary>把本地状态和服务器状态合到一张卡上；服务器不通也不算失败。</summary>
        public async Task<MarketStatusInfo> CheckStatusAsync()
        {
            var info = LocalStatus();
            try
            {
                var json = await OnlineHttp.GetStringAsync(_site + "/pcv-api/status.json").ConfigureAwait(false);
                var doc = JsonConvert.DeserializeObject<ServerStatus>(json);
                if (doc == null || doc.Schema != 1)
                    throw new InvalidDataException("服务状态格式不对（可能被截断）");
                info.ServerAvailable = true;
                info.ServerMarketEnabled = doc.MarketEnabled;
                info.ServerItems = doc.MarketItems;
                info.Notice = doc.Notice ?? "";
                info.Updated = string.IsNullOrEmpty(doc.Updated) ? info.Updated : doc.Updated;
            }
            catch (Exception ex)
            {
                info.ServerAvailable = false;
                info.Message = string.IsNullOrEmpty(info.Message)
                    ? "服务器状态读不到：" + OnlineHttp.Short(ex.Message) : info.Message;
            }
            return info;
        }

        public Task<MarketResult> ForceRefreshAsync() => _market.LoadAsync(true);

        public bool ClearCache() => _market.ClearCache();

        private sealed class ServerStatus
        {
            public int Schema { get; set; }
            public bool MarketEnabled { get; set; }
            public int MarketItems { get; set; }
            public string Notice { get; set; }
            public string Updated { get; set; }
        }
    }

    /// <summary>
    /// 自动更新：同时问博客的 version.json 和 GitHub releases/latest。
    /// 只比较、不静默下载 —— 有新版本就引导用户去 GitHub 自己拿安装包。
    /// 两个通道各自独立，挂一个不影响另一个；都挂了就如实说查不了。
    /// </summary>
    public sealed class UpdateService
    {
        private const string ReleaseUrl = "https://github.com/150311-ma/PCV/releases/latest";
        private const string GitHubApi = "https://api.github.com/repos/150311-ma/PCV/releases/latest";
        private readonly string _site;
        private readonly string _currentVersion;

        public UpdateService(string currentVersion, IApiGateway gateway)
        {
            _currentVersion = currentVersion ?? "0.0.0";
            _site = gateway != null && !string.IsNullOrWhiteSpace(gateway.Site)
                ? gateway.Site.TrimEnd('/') : "https://cyan-blog-2fk.pages.dev";
        }

        public string ReleasePage => ReleaseUrl;

        public async Task<UpdateInfo> CheckAsync()
        {
            var info = new UpdateInfo
            {
                CurrentVersion = _currentVersion,
                ReleaseUrl = ReleaseUrl,
                ServerVersion = "—",
                GitHubVersion = "—",
                LatestVersion = _currentVersion,
            };

            var server = OnlineHttp.GetStringAsync(_site + "/pcv-api/version.json");
            var github = OnlineHttp.GetStringAsync(GitHubApi, "PCV-Update/1");
            await Task.WhenAll(server, github).ConfigureAwait(false);

            var notes = "";
            try
            {
                var doc = JsonConvert.DeserializeObject<VersionDocument>(await server.ConfigureAwait(false));
                if (doc != null && !string.IsNullOrWhiteSpace(doc.Version)) info.ServerVersion = doc.Version.Trim();
                else notes += "服务端版本读不出来；";
            }
            catch (Exception ex) { notes += "服务端：" + OnlineHttp.Short(ex.Message) + "；"; }

            try
            {
                var doc = JsonConvert.DeserializeObject<GitHubRelease>(await github.ConfigureAwait(false));
                if (doc != null && !string.IsNullOrWhiteSpace(doc.TagName))
                    info.GitHubVersion = doc.TagName.Trim().TrimStart('v', 'V');
                else notes += "GitHub 版本读不出来；";
            }
            catch (Exception ex) { notes += "GitHub：" + OnlineHttp.Short(ex.Message) + "；"; }

            var best = _currentVersion;
            foreach (var candidate in new[] { info.ServerVersion, info.GitHubVersion })
                if (Newer(candidate, best)) best = candidate;
            info.LatestVersion = best;
            info.HasUpdate = Newer(best, _currentVersion);
            info.Message = info.HasUpdate
                ? $"有新版本 {best}（当前 {_currentVersion}），点下面的按钮去 GitHub 下载。"
                : (notes.Length > 0 ? "已是最新版；" + notes : "已是最新版，两个通道都问过了。");
            return info;
        }

        public void OpenRelease()
        {
            try
            {
                Process.Start(new ProcessStartInfo(ReleaseUrl) { UseShellExecute = true });
            }
            catch (Exception ex)
            {
                throw new InvalidOperationException("打不开浏览器：" + ex.Message, ex);
            }
        }

        /// <summary>三段数字比较；格式不合法的一律当作「不比当前新」。</summary>
        internal static bool Newer(string candidate, string current)
        {
            int[] a, b;
            if (!TryParse(candidate, out a) || !TryParse(current, out b)) return false;
            for (var i = 0; i < 3; i++)
            {
                if (a[i] != b[i]) return a[i] > b[i];
            }
            return false;
        }

        private static bool TryParse(string text, out int[] parts)
        {
            parts = new int[3];
            if (string.IsNullOrWhiteSpace(text)) return false;
            var bits = text.Trim().Split('.');
            if (bits.Length != 3) return false;
            for (var i = 0; i < 3; i++)
                if (!int.TryParse(bits[i], out parts[i])) return false;
            return true;
        }

        private sealed class VersionDocument
        {
            public string Version { get; set; }
        }

        private sealed class GitHubRelease
        {
            [JsonProperty("tag_name")] public string TagName { get; set; }
        }
    }

    /// <summary>共用的小 HTTP 客户端：CF 那段会偶发截断，重试到能解析为止。</summary>
    internal static class OnlineHttp
    {
        private const int TimeoutMs = 10000;
        private const int MaxAttempts = 3;

        public static Task<string> GetStringAsync(string url, string userAgent = "PCV-Online/1")
            => Task.Run(() => Get(url, userAgent));

        private static string Get(string url, string userAgent)
        {
            Exception last = null;
            for (var attempt = 1; attempt <= MaxAttempts; attempt++)
            {
                try
                {
                    ServicePointManager.SecurityProtocol |= SecurityProtocolType.Tls12;
                    var request = (HttpWebRequest)WebRequest.Create(
                        url + (url.Contains("?") ? "&" : "?") + "n=" + attempt);
                    request.Method = "GET";
                    request.Timeout = TimeoutMs;
                    request.ReadWriteTimeout = TimeoutMs;
                    request.UserAgent = userAgent;
                    request.Accept = "application/json";
                    request.Headers[HttpRequestHeader.CacheControl] = "no-cache";
                    using (var response = (HttpWebResponse)request.GetResponse())
                    using (var stream = response.GetResponseStream())
                    using (var reader = new StreamReader(stream, Encoding.UTF8))
                    {
                        var text = reader.ReadToEnd();
                        if (string.IsNullOrWhiteSpace(text))
                            throw new InvalidDataException("响应是空的");
                        // 半截 JSON 会在这里炸掉，交给下一次重试
                        JsonConvert.DeserializeObject(text);
                        return text;
                    }
                }
                catch (Exception ex) { last = ex; }
            }
            throw last ?? new InvalidOperationException("请求失败");
        }

        public static string Short(string text)
        {
            if (string.IsNullOrEmpty(text)) return "原因不明";
            var one = text.Replace("\r", " ").Replace("\n", " ");
            return one.Length > 80 ? one.Substring(0, 80) + "…" : one;
        }
    }
}
