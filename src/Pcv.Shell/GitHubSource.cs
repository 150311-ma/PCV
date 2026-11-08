using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Text;
using System.Threading.Tasks;
using Newtonsoft.Json;

namespace Pcv.Shell
{
    /// <summary>
    /// 社区市场：模块放在各人自己的 GitHub 仓库里，没有中心服务器。
    ///
    /// 约定（创作者工具会自动按这个格式发，不用手写）：
    ///   1. 仓库打上 topic `pcv-module`；
    ///   2. 仓库根目录放 `pcv-module.json`（一份清单，格式见 ManualDocument）；
    ///   3. `.pclpkg` 放在仓库里（或 Release 资产里，这里走仓库原文）。
    ///
    /// 查找方式：先按模块名搜 GitHub 仓库（限定 topic），再逐个读它们的
    /// pcv-module.json。看不懂、字段不全、模块数字对不上的，直接跳过 ——
    /// 别人的仓库改不了，只能不信任。
    ///
    /// 为什么不走中心索引：索引本身就要有人维护、要存储、要审核，
    /// 那就又回到「谁来做服务器」的问题。仓库自己就是存储。
    /// </summary>
    public static class GitHubSource
    {
        public const string Topic = "pcv-module";
        public const string ManifestName = "pcv-module.json";

        private const int TimeoutMs = 12000;
        private const int MaxAttempts = 2;
        /// <summary>最近一次搜索的说明，给界面显示（配额、命中数这些）。</summary>
        public static string LastNote { get; private set; } = "";

        /// <summary>仓库里某个文件的原文地址。</summary>
        public static string RawUrl(string repo, string branch, string file)
        {
            var name = string.IsNullOrWhiteSpace(branch) ? "main" : branch.Trim();
            return "https://raw.githubusercontent.com/" + repo.Trim().Trim('/') + "/"
                   + Uri.EscapeDataString(name) + "/" + Uri.EscapeDataString(file);
        }

        /// <summary>
        /// 同一个文件的 API 地址。
        ///
        /// 为什么要有第二条路：raw.githubusercontent.com 在国内经常抽风
        /// （TLS 握手过了、正文一个字节都不回），而 api.github.com 通常是通的。
        /// 两条路互备，任一条拿到就算成功 —— 少一个用户就要怀疑「市场坏了」。
        /// </summary>
        public static string ApiFileUrl(string repo, string branch, string file)
        {
            var name = string.IsNullOrWhiteSpace(branch) ? "main" : branch.Trim();
            return "https://api.github.com/repos/" + repo.Trim().Trim('/') + "/contents/"
                   + Uri.EscapeDataString(file) + "?ref=" + Uri.EscapeDataString(name);
        }

        /// <summary>按模块名搜社区模块。关键词为空时列出全部（受配额限制，最多 30 个仓库）。</summary>
        public static Task<List<MarketItem>> SearchAsync(string keyword)
            => Task.Run(() => Search(keyword));

        private static List<MarketItem> Search(string keyword)
        {
            var items = new List<MarketItem>();
            var keywordText = (keyword ?? "").Trim();

            string searchJson;
            try
            {
                // 按 topic 搜仓库；有关键词时一并带上，让 GitHub 先筛一遍
                var query = "topic:" + Topic + (keywordText.Length > 0 ? " " + keywordText : "");
                var url = "https://api.github.com/search/repositories?per_page=30&q="
                          + Uri.EscapeDataString(query);
                searchJson = Get(url, "PCV-Market/4");
            }
            catch (Exception ex)
            {
                LastNote = "GitHub 这会儿连不上：" + Short(ex.Message);
                return items;
            }

            SearchResponse response;
            try { response = JsonConvert.DeserializeObject<SearchResponse>(searchJson); }
            catch { LastNote = "GitHub 返回的内容读不懂（可能被截断）"; return items; }

            var repos = response?.Items;
            if (repos == null || repos.Count == 0)
            {
                LastNote = keywordText.Length > 0
                    ? "GitHub 上没搜到名字像「" + keywordText + "」的模块。"
                    : "GitHub 上还没有打过 pcv-module 标记的仓库。";
                return items;
            }

            var skipped = 0;
            foreach (var repo in repos)
            {
                if (repo == null || string.IsNullOrWhiteSpace(repo.FullName)) { skipped++; continue; }
                var item = ReadManifest(repo);
                if (item == null) { skipped++; continue; }
                items.Add(item);
            }

            LastNote = "GitHub 上找到 " + repos.Count + " 个仓库，"
                       + items.Count + " 个能认（其余 " + skipped + " 个没有 "
                       + ManifestName + " 或格式不对）";
            return items;
        }

        /// <summary>读某个仓库的 pcv-module.json，转成市场条目。读不出来返回 null。</summary>
        private static MarketItem ReadManifest(RepoInfo repo)
        {
            var branch = string.IsNullOrWhiteSpace(repo.DefaultBranch) ? "main" : repo.DefaultBranch;
            var json = GetRawOrApi(repo.FullName, branch, ManifestName);
            if (json == null) return null;

            ManualDocument doc;
            try { doc = JsonConvert.DeserializeObject<ManualDocument>(json); }
            catch { return null; }
            if (doc == null) return null;

            // 字段不全的一律不认：宁可少显示一个，也不要显示一个装不上的
            if (string.IsNullOrWhiteSpace(doc.Id) || string.IsNullOrWhiteSpace(doc.Name)) return null;
            if (string.IsNullOrWhiteSpace(doc.Version) || string.IsNullOrWhiteSpace(doc.File)) return null;
            if (!MarketService.IsPlainFileName(doc.File)) return null;
            if (string.IsNullOrWhiteSpace(doc.Sha256) || doc.Sha256.Trim().Length != 64) return null;

            return new MarketItem
            {
                Id = doc.Id.Trim(),
                Name = doc.Name.Trim(),
                Version = doc.Version.Trim(),
                Author = string.IsNullOrWhiteSpace(doc.Author) ? repo.Owner?.Login : doc.Author.Trim(),
                Summary = doc.Summary ?? "",
                Category = string.IsNullOrWhiteSpace(doc.Category) ? "社区" : doc.Category.Trim(),
                Icon = doc.Icon,
                Accent = doc.Accent,
                Official = false,
                File = doc.File.Trim(),
                Size = doc.Size,
                Sha256 = doc.Sha256.Trim().ToLowerInvariant(),
                ModuleCode = doc.ModuleCode,
                Repo = repo.FullName,
                Branch = branch,
                Tags = doc.Tags ?? new List<string>(),
                Source = "GitHub · " + repo.FullName,
                Offline = false,
            };
        }

        /// <summary>
        /// 先走 raw，失败退到 api.github.com。两条都不通才返回 null。
        ///
        /// API 那条要带 Accept: application/vnd.github.raw 才会回文件原文，
        /// 否则回的是 base64 的 JSON 包装。
        /// </summary>
        internal static string GetRawOrApi(string repo, string branch, string file)
        {
            try { return Get(RawUrl(repo, branch, file), "PCV-Market/4"); }
            catch { }

            try
            {
                return Get(ApiFileUrl(repo, branch, file), "PCV-Market/4",
                    "application/vnd.github.raw");
            }
            catch { return null; }
        }

        private static string Get(string url, string userAgent, string accept = "application/vnd.github+json")
        {
            Exception last = null;
            for (var attempt = 1; attempt <= MaxAttempts; attempt++)
            {
                try
                {
                    ServicePointManager.SecurityProtocol |= SecurityProtocolType.Tls12;
                    var request = (HttpWebRequest)WebRequest.Create(url);
                    request.Method = "GET";
                    request.Timeout = TimeoutMs;
                    request.ReadWriteTimeout = TimeoutMs;
                    request.UserAgent = userAgent;
                    request.Accept = accept;
                    request.Headers[HttpRequestHeader.CacheControl] = "no-cache";
                    // API 路径要带版本头，不然哪天接口变了会静默走另一套行为
                    if (url.IndexOf("api.github.com", StringComparison.OrdinalIgnoreCase) >= 0)
                        request.Headers["X-GitHub-Api-Version"] = "2022-11-28";
                    using (var response = (HttpWebResponse)request.GetResponse())
                    using (var stream = response.GetResponseStream())
                    using (var reader = new StreamReader(stream, Encoding.UTF8))
                    {
                        var text = reader.ReadToEnd();
                        if (string.IsNullOrWhiteSpace(text)) throw new InvalidDataException("响应是空的");
                        return text;
                    }
                }
                catch (WebException ex) when (ex.Response is HttpWebResponse error
                                              && (int)error.StatusCode == 403)
                {
                    // 配额用完不是「网坏了」，说清楚免得用户反复点
                    throw new InvalidDataException("GitHub 搜索次数用完了，过一会儿再试", ex);
                }
                catch (Exception ex) { last = ex; }
            }
            throw last ?? new InvalidOperationException("请求失败");
        }

        private static string Short(string text)
        {
            if (string.IsNullOrEmpty(text)) return "原因不明";
            var one = text.Replace("\r", " ").Replace("\n", " ");
            return one.Length > 80 ? one.Substring(0, 80) + "…" : one;
        }

        // ── GitHub 返回的 JSON 只取用得到的字段 ────────────────────────────

        private sealed class SearchResponse
        {
            [JsonProperty("items")] public List<RepoInfo> Items { get; set; }
        }

        private sealed class RepoInfo
        {
            [JsonProperty("full_name")] public string FullName { get; set; }
            [JsonProperty("default_branch")] public string DefaultBranch { get; set; }
            [JsonProperty("owner")] public RepoOwner Owner { get; set; }
        }

        private sealed class RepoOwner
        {
            [JsonProperty("login")] public string Login { get; set; }
        }

        /// <summary>创作者发到仓库根目录的 pcv-module.json。</summary>
        internal sealed class ManualDocument
        {
            [JsonProperty("schema")] public int Schema { get; set; } = 1;
            [JsonProperty("id")] public string Id { get; set; }
            [JsonProperty("name")] public string Name { get; set; }
            [JsonProperty("version")] public string Version { get; set; }
            [JsonProperty("author")] public string Author { get; set; }
            [JsonProperty("summary")] public string Summary { get; set; }
            [JsonProperty("category")] public string Category { get; set; }
            [JsonProperty("icon")] public string Icon { get; set; }
            [JsonProperty("accent")] public string Accent { get; set; }
            [JsonProperty("file")] public string File { get; set; }
            [JsonProperty("size")] public long Size { get; set; }
            [JsonProperty("sha256")] public string Sha256 { get; set; }
            [JsonProperty("moduleCode")] public string ModuleCode { get; set; }
            [JsonProperty("tags")] public List<string> Tags { get; set; }
        }
    }
}
