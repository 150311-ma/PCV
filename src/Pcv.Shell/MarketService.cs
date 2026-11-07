using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Threading.Tasks;
using Newtonsoft.Json;

namespace Pcv.Shell
{
    /// <summary>市场清单里的一条。字段名与博客上的 market/index.json 一一对应。</summary>
    public sealed class MarketItem
    {
        public string Id { get; set; }
        public string Name { get; set; }
        public string Version { get; set; }
        public string Author { get; set; }
        public string Summary { get; set; }
        public string Category { get; set; }
        public string Icon { get; set; }
        public string Accent { get; set; }
        public bool Official { get; set; }
        public string File { get; set; }
        public long Size { get; set; }
        public string Sha256 { get; set; }
        public List<string> Tags { get; set; } = new List<string>();

        [JsonIgnore] public string Source { get; set; }
        [JsonIgnore] public bool Offline { get; set; }

        public string SizeText => Size <= 0 ? "" : (Size / 1024.0).ToString("0.#") + " KB";
        public string IconText => string.IsNullOrWhiteSpace(Icon)
            ? (string.IsNullOrEmpty(Name) ? "P" : Name.Substring(0, 1))
            : Icon.Trim();
    }

    public sealed class MarketCatalog
    {
        public int Schema { get; set; } = 1;
        public string Updated { get; set; }
        public string Site { get; set; }
        public List<MarketItem> Items { get; set; } = new List<MarketItem>();
    }

    /// <summary>拉取清单的结果：数据 + 从哪来的 + 出错原因。</summary>
    public sealed class MarketResult
    {
        public bool Success { get; set; }
        public MarketCatalog Catalog { get; set; }
        public string Origin { get; set; }
        public string Message { get; set; }
    }

    /// <summary>
    /// 市场客户端。博客是服务端：Cloudflare Pages 上的静态 index.json + .pclpkg。
    ///
    /// 三条取清单的路，按顺序退：
    ///   线上 → 本地缓存 → 程序集里内置的那一份（和博客同源，构建时打进去）。
    /// 断网不是错误状态，市场照样能看，只是「安装」会提示等联网。
    ///
    /// 安全：下载完先验 sha256 与 size，再交给 PackageInstaller 完整体检。
    /// 清单里的 file 只允许纯文件名（工具脚本会闸住），杜绝拼成任意 URL。
    /// </summary>
    public sealed class MarketService
    {
        public const string MarketUrl = "https://cyan-blog-2fk.pages.dev/market/index.json";
        private const string EmbeddedCatalog = "PCV.market-index.json";
        private const int TimeoutMs = 10000;
        /// <summary>
        /// 本机到 Cloudflare 这段路径实测会抖动：同一端点连打几次，偶尔返回
        /// 截断的正文（HTTP 200、Content-Length 对，但只收到几百字节就断）。
        /// 不重试的话用户会看到「连不上市场」，其实再试一次就好。
        /// 每次都按「必须能解析成完整清单」判定，不靠状态码自欺。
        /// </summary>
        private const int MaxAttempts = 3;

        private readonly string _cacheFile;

        public MarketService(string dataDirectory)
        {
            _cacheFile = Path.Combine(dataDirectory, "market-cache.json");
        }

        public string BaseUrl => MarketUrl.Substring(0, MarketUrl.LastIndexOf('/') + 1);

        // ── 清单 ────────────────────────────────────────────────────────────

        public Task<MarketResult> LoadAsync(bool forceOnline = true)
            => Task.Run(() => Load(forceOnline));

        /// <summary>同步版，只给离屏渲染截图用（异步还没回来就截图会拍到空列表）。</summary>
        public MarketResult LoadForRender() => Load(true);

        private MarketResult Load(bool forceOnline)
        {
            if (forceOnline)
            {
                string lastError = "原因不明";
                for (var attempt = 1; attempt <= MaxAttempts; attempt++)
                {
                    try
                    {
                        var json = DownloadString(MarketUrl + "?t=" + DateTime.UtcNow.Ticks + "&n=" + attempt);
                        var catalog = Parse(json);
                        if (catalog == null || catalog.Items.Count == 0)
                            throw new InvalidDataException("清单是空的或格式不对（可能被截断）");
                        foreach (var item in catalog.Items) { item.Source = "线上"; item.Offline = false; }
                        TryCache(json);
                        return new MarketResult
                        {
                            Success = true, Catalog = catalog, Origin = "线上市场",
                            Message = attempt > 1 ? $"第 {attempt} 次才拿到完整清单" : ""
                        };
                    }
                    catch (Exception ex)
                    {
                        lastError = Short(ex.Message);
                    }
                }
                return Fallback("连不上市场（试了 " + MaxAttempts + " 次），先看上次的内容：" + lastError);
            }
            return Fallback("用的本地内容");
        }

        private MarketResult Fallback(string reason)
        {
            // 缓存优先：它可能是更新过的线上版本
            try
            {
                if (System.IO.File.Exists(_cacheFile))
                {
                    var cached = Parse(System.IO.File.ReadAllText(_cacheFile, new UTF8Encoding(false)));
                    if (cached != null && cached.Items.Count > 0)
                    {
                        foreach (var item in cached.Items) { item.Source = "本地缓存"; item.Offline = true; }
                        return new MarketResult
                        {
                            Success = true, Catalog = cached, Origin = "本地缓存",
                            Message = reason
                        };
                    }
                }
            }
            catch { /* 缓存坏了继续往内置那份退 */ }

            try
            {
                var embedded = ReadEmbedded();
                if (embedded != null)
                {
                    var catalog = Parse(embedded);
                    if (catalog != null && catalog.Items.Count > 0)
                    {
                        foreach (var item in catalog.Items) { item.Source = "内置清单"; item.Offline = true; }
                        return new MarketResult
                        {
                            Success = true, Catalog = catalog, Origin = "内置清单",
                            Message = reason
                        };
                    }
                }
            }
            catch { /* 最后再报失败 */ }

            return new MarketResult { Success = false, Origin = "不可用", Message = reason };
        }

        private static MarketCatalog Parse(string json)
        {
            var catalog = JsonConvert.DeserializeObject<MarketCatalog>(json);
            if (catalog == null) return null;
            if (catalog.Items == null) catalog.Items = new List<MarketItem>();
            // 只留字段完整的条目；坏条目不该让整页打不开
            catalog.Items.RemoveAll(i => string.IsNullOrWhiteSpace(i.Id)
                                        || string.IsNullOrWhiteSpace(i.File)
                                        || !IsPlainFileName(i.File));
            return catalog;
        }

        internal static bool IsPlainFileName(string file)
        {
            if (string.IsNullOrWhiteSpace(file)) return false;
            if (file.IndexOf('/') >= 0 || file.IndexOf('\\') >= 0 || file.IndexOf("..", StringComparison.Ordinal) >= 0)
                return false;
            return file.EndsWith(".pclpkg", StringComparison.OrdinalIgnoreCase);
        }

        private void TryCache(string json)
        {
            try
            {
                var folder = Path.GetDirectoryName(_cacheFile);
                if (!string.IsNullOrEmpty(folder)) Directory.CreateDirectory(folder);
                System.IO.File.WriteAllText(_cacheFile, json, new UTF8Encoding(false));
            }
            catch { /* 缓存写不上不影响这次使用 */ }
        }

        private static string ReadEmbedded()
        {
            var assembly = typeof(MarketService).Assembly;
            using (var stream = assembly.GetManifestResourceStream(EmbeddedCatalog))
            {
                if (stream == null) return null;
                using (var reader = new StreamReader(stream, Encoding.UTF8))
                    return reader.ReadToEnd();
            }
        }

        // ── 下载包 ──────────────────────────────────────────────────────────

        /// <summary>下载到临时目录并校验。返回本地文件路径；失败抛异常，由 UI 显示原因。</summary>
        public Task<string> DownloadAsync(MarketItem item)
            => Task.Run(() => Download(item));

        private string Download(MarketItem item)
        {
            if (item == null || !IsPlainFileName(item.File))
                throw new InvalidDataException("清单里的文件名不合法");

            var folder = Path.Combine(Path.GetTempPath(), "pcv-market");
            Directory.CreateDirectory(folder);
            var target = Path.Combine(folder, item.File);
            var url = BaseUrl + Uri.EscapeDataString(item.File);

            string lastError = "原因不明";
            for (var attempt = 1; attempt <= MaxAttempts; attempt++)
            {
                try
                {
                    DownloadFile(url + "?n=" + attempt, target);
                    var info = new FileInfo(target);
                    if (item.Size > 0 && info.Length != item.Size)
                        throw new InvalidDataException($"大小和清单对不上（{info.Length} ≠ {item.Size}）");
                    if (!string.IsNullOrWhiteSpace(item.Sha256))
                    {
                        var actual = Sha256(target);
                        if (!string.Equals(actual, item.Sha256.Trim(), StringComparison.OrdinalIgnoreCase))
                            throw new InvalidDataException("校验和不匹配");
                    }
                    return target;
                }
                catch (Exception ex)
                {
                    lastError = Short(ex.Message);
                    try { if (System.IO.File.Exists(target)) System.IO.File.Delete(target); }
                    catch { /* 删不掉覆盖写也行 */ }
                }
            }
            throw new InvalidDataException($"下载 {MaxAttempts} 次都没拿到完整的包：" + lastError);
        }

        internal static string Sha256(string path)
        {
            using (var sha = SHA256.Create())
            using (var stream = System.IO.File.OpenRead(path))
            {
                var bytes = sha.ComputeHash(stream);
                var text = new StringBuilder(bytes.Length * 2);
                foreach (var b in bytes) text.Append(b.ToString("x2"));
                return text.ToString();
            }
        }

        // ── HTTP（只用 BCL，不引第三方）────────────────────────────────────

        private static HttpWebRequest Request(string url)
        {
            ServicePointManager.SecurityProtocol |= SecurityProtocolType.Tls12;
            var request = (HttpWebRequest)WebRequest.Create(url);
            request.Timeout = TimeoutMs;
            request.ReadWriteTimeout = TimeoutMs;
            request.UserAgent = "PCV-Market/2.10";
            request.Headers[HttpRequestHeader.CacheControl] = "no-cache";
            return request;
        }

        private static string DownloadString(string url)
        {
            using (var response = (HttpWebResponse)Request(url).GetResponse())
            using (var stream = response.GetResponseStream())
            using (var reader = new StreamReader(stream, Encoding.UTF8))
                return reader.ReadToEnd();
        }

        private static void DownloadFile(string url, string target)
        {
            using (var response = (HttpWebResponse)Request(url).GetResponse())
            using (var stream = response.GetResponseStream())
            using (var file = System.IO.File.Create(target))
                stream.CopyTo(file);
        }

        private static string Short(string message)
        {
            if (string.IsNullOrEmpty(message)) return "原因不明";
            var oneLine = message.Replace("\r", " ").Replace("\n", " ");
            return oneLine.Length > 90 ? oneLine.Substring(0, 90) + "…" : oneLine;
        }
    }
}
