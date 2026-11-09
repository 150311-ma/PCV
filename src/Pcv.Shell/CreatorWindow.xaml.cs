using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net;
using System.Text;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Microsoft.Win32;
using Newtonsoft.Json;
using Pcv.Modular.Packages;

namespace Pcv.Shell
{
    /// <summary>
    /// 创作者工具：把做好的模块发到自己的 GitHub 仓库，顺带算出一个模块数字。
    ///
    /// 为什么要有它：市场里除了官方包，还应该能装别人做的。但 PCV 没有服务器
    /// 存这些包 —— 那就让创作者用自己的仓库，PCV 只负责「按名字去 GitHub 上找」。
    /// 这个窗口把这些麻烦事包成三步：选模块 → 填仓库和令牌 → 上传。
    ///
    /// 上传只做三件事：
    ///   1. 把 .pclpkg 传进仓库；
    ///   2. 写 / 更新根目录的 pcv-module.json（市场靠它认模块）；
    ///   3. 给仓库打 topic `pcv-module`（市场靠它搜得到）。
    ///
    /// 令牌只在这台机器上用一次，不落盘到仓库、不进日志、不上传别处。
    /// </summary>
    public partial class CreatorWindow : Window
    {
        private const string RepoKey = "CreatorGitHubRepo";
        private const string ApiRoot = "https://api.github.com";

        private readonly Action _onPublished;
        private CreatorPackage _current;

        public CreatorWindow()
        {
            InitializeComponent();
            TryRestoreRepo();
        }

        /// <summary>发布成功后的回调：让主窗口刷新市场。</summary>
        public CreatorWindow(Action onPublished) : this()
        {
            _onPublished = onPublished;
        }

        // ── 第 1 步：选模块 ────────────────────────────────────────────────

        private void BtnPickPackage_Click(object sender, RoutedEventArgs e)
        {
            var dialog = new OpenFileDialog
            {
                Title = "选一个打包好的模块",
                Filter = "PCV 模块包 (*.pclpkg;*.zip)|*.pclpkg;*.zip|所有文件 (*.*)|*.*",
            };
            if (dialog.ShowDialog(this) != true) return;
            InspectPackage(dialog.FileName);
        }

        private void BtnPickFolder_Click(object sender, RoutedEventArgs e)
        {
            // .NET Framework 4.8 的 WPF 没有 OpenFolderDialog，用 WinForms 那个
            using (var dialog = new System.Windows.Forms.FolderBrowserDialog())
            {
                dialog.Description = "选模块目录（里面有 module.json）";
                dialog.ShowNewFolderButton = false;
                if (dialog.ShowDialog() != System.Windows.Forms.DialogResult.OK) return;

                // 目录不是包，先就地打成临时包再当包看 —— 不给两条不同的校验路
                try
                {
                    var manifestPath = Path.Combine(dialog.SelectedPath, "module.json");
                    if (!File.Exists(manifestPath))
                    {
                        MessageBox.Show(this, "这个目录里没有 module.json。\n\n选模块源码构建出来的输出目录（bin/Release 那一层），" +
                                              "或者用「选包」直接选 .pclpkg。",
                            "创作者工具", MessageBoxButton.OK, MessageBoxImage.Warning);
                        return;
                    }

                    var target = Path.Combine(Path.GetTempPath(), "pcv-creator",
                        "pack-" + DateTime.Now.ToString("HHmmss") + ".pclpkg");
                    Directory.CreateDirectory(Path.GetDirectoryName(target));
                    PackFolder(dialog.SelectedPath, target);
                    InspectPackage(target);
                }
                catch (Exception ex)
                {
                    MessageBox.Show(this, "打包这个目录时出错：" + ex.Message,
                        "创作者工具", MessageBoxButton.OK, MessageBoxImage.Warning);
                }
            }
        }

        /// <summary>把模块目录打成 .pclpkg。规则与 tools/pack_module.py 一致：只收白名单扩展名。</summary>
        private static void PackFolder(string folder, string target)
        {
            var allowed = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            {
                ".dll", ".json", ".txt", ".md", ".xaml", ".png", ".jpg", ".ico", ".xml",
                ".py", ".js", ".mjs", ".ts", ".lua", ".rb", ".ps1", ".sh", ".cmd", ".bat",
                ".exe", ".jar", ".node", ".html", ".css", ".csv", ".yml", ".yaml", ".toml",
            };

            using (var archive = ZipFile.Open(target, ZipArchiveMode.Create))
            {
                foreach (var path in Directory.EnumerateFiles(folder, "*", SearchOption.AllDirectories))
                {
                    if (!allowed.Contains(Path.GetExtension(path))) continue;
                    var relative = path.Substring(folder.Length).TrimStart('\\', '/').Replace('\\', '/');
                    archive.CreateEntryFromFile(path, relative, CompressionLevel.Optimal);
                }
            }
        }

        /// <summary>读包、算数字、填界面。所有校验都走安装器那一套，不另写一份。</summary>
        private void InspectPackage(string path)
        {
            try
            {
                var inspection = CreatorPackage.Inspect(path);
                if (!inspection.Ok)
                {
                    MessageBox.Show(this, "这个包还不能发：\n\n" + inspection.Problem,
                        "创作者工具", MessageBoxButton.OK, MessageBoxImage.Warning);
                    return;
                }

                _current = inspection;
                TxtPackage.Text = path;

                PanModule.Visibility = Visibility.Visible;
                LabIcon.Text = string.IsNullOrWhiteSpace(inspection.IconText) ? "模" : inspection.IconText;
                LabModuleName.Text = inspection.Name;
                LabModuleVersion.Text = inspection.Version;
                LabModuleId.Text = inspection.Id + "　·　" + inspection.EntryCount + " 个文件　·　"
                                   + (inspection.Size / 1024.0).ToString("0.#") + " KB";
                LabModuleSummary.Text = inspection.Summary;
                LabModuleCode.Text = inspection.Code;
                LabFingerprint.Text = "包指纹 sha256：" + inspection.Sha256 + "\n文件名：" + inspection.FileName;

                BtnUpload.IsEnabled = true;
                LabHint.Text = "模块读出来了。填好仓库和令牌就能发。";
                LabBusy.Text = "";
            }
            catch (Exception ex)
            {
                MessageBox.Show(this, "读这个包时出错：" + ex.Message,
                    "创作者工具", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
        }

        private void BtnCopyCode_Click(object sender, RoutedEventArgs e)
        {
            if (_current == null) return;
            try
            {
                Clipboard.SetText(_current.Code);
                LabHint.Text = "模块数字已复制：可以贴进简介、发给别人核对。";
            }
            catch { LabHint.Text = "剪贴板被别的程序占着，再点一次试试。"; }
        }

        /// <summary>给离屏渲染用：不弹任何对话框，直接把包读进来填好界面。</summary>
        internal void LoadPackageForRender(string packagePath)
        {
            try
            {
                var inspection = CreatorPackage.Inspect(packagePath);
                if (!inspection.Ok) return;
                _current = inspection;
                TxtPackage.Text = packagePath;
                PanModule.Visibility = Visibility.Visible;
                LabIcon.Text = string.IsNullOrWhiteSpace(inspection.IconText) ? "模" : inspection.IconText;
                LabModuleName.Text = inspection.Name;
                LabModuleVersion.Text = inspection.Version;
                LabModuleId.Text = inspection.Id + "　·　" + inspection.EntryCount + " 个文件　·　"
                                   + (inspection.Size / 1024.0).ToString("0.#") + " KB";
                LabModuleSummary.Text = inspection.Summary;
                LabModuleCode.Text = inspection.Code;
                LabFingerprint.Text = "包指纹 sha256：" + inspection.Sha256 + "\n文件名：" + inspection.FileName;
                BtnUpload.IsEnabled = true;
                TxtRepo.Text = "yourname/pcv-my-module";
                LabHint.Text = "模块读出来了。填好仓库和令牌就能发。";
            }
            catch { /* 渲染用，出问题就当没填 */ }
        }

        // ── 第 2 步：上传 ──────────────────────────────────────────────────

        private async void BtnUpload_Click(object sender, RoutedEventArgs e)
        {
            if (_current == null) return;

            var token = TxtToken.Password.Trim();
            var repo = TxtRepo.Text.Trim().Trim('/');
            var branch = string.IsNullOrWhiteSpace(TxtBranch.Text) ? "main" : TxtBranch.Text.Trim();

            if (token.Length == 0)
            {
                MessageBox.Show(this, "先填 GitHub 访问令牌。\n\n没有的话点「打开我的令牌页」，" +
                                      "建一个 classic token，勾上 repo 那一项就行。",
                    "创作者工具", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }
            if (!repo.Contains("/") || repo.Split('/').Length != 2)
            {
                MessageBox.Show(this, "仓库要写成 owner/name，例如 yourname/pcv-my-module。",
                    "创作者工具", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            BtnUpload.IsEnabled = false;
            PanResult.Visibility = Visibility.Collapsed;

            try
            {
                LabBusy.Text = "正在连接 GitHub…";
                var publisher = new GitHubPublisher(token);
                var steps = new List<string>();
                var progress = new Progress<string>(line => { LabBusy.Text = line; steps.Add(line); });

                var result = await publisher.PublishAsync(_current, repo, branch, progress);

                RememberRepo(repo);

                PanResult.Visibility = Visibility.Visible;
                LabResultTitle.Text = result.Updated ? "更新好了" : "发布好了";
                var removedLine = result.Removed.Count == 0
                    ? ""
                    : "\n顺手清掉了 " + result.Removed.Count + " 个旧版包：" + string.Join("、", result.Removed);
                LabResultBody.Text =
                    "仓库：" + result.Repo + "（" + result.Branch + "）\n"
                    + "模块数字：" + _current.Code + "\n"
                    + "别人在市场里切到 GitHub，搜「" + _current.Name + "」就能找到。\n"
                    + "仓库里现在有：" + string.Join("、", result.Files) + removedLine;
                LabHint.Text = "发好了。把模块数字发给朋友，他们也能核对拿到的是不是这一份。";
                LabBusy.Text = "";

                _onPublished?.Invoke();
            }
            catch (Exception ex)
            {
                var message = ex is GitHubException gh ? gh.Friendly : ex.Message;
                MessageBox.Show(this, "没发出去：\n\n" + message + "\n\n" + Steps(),
                    "创作者工具", MessageBoxButton.OK, MessageBoxImage.Warning);
                LabHint.Text = "没发出去，原因见弹窗。";
                LabBusy.Text = "";
            }
            finally
            {
                BtnUpload.IsEnabled = true;
            }

            string Steps() => "令牌要能往 " + repo + " 写东西（classic token 勾 repo）；另外仓库得先建好，或者勾上要建的那个。";
        }

        private void BtnOpenTokenPage_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(
                    "https://github.com/settings/tokens/new?scopes=repo&description=PCV%20Creator")
                { UseShellExecute = true });
            }
            catch (Exception ex)
            {
                MessageBox.Show(this, "打不开浏览器：" + ex.Message,
                    "创作者工具", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
        }

        private void BtnUseSavedToken_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                var saved = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                    "PCV", "creator-token.txt");
                if (!File.Exists(saved))
                {
                    MessageBox.Show(this, "这台机器上还没有保存过令牌。",
                        "创作者工具", MessageBoxButton.OK, MessageBoxImage.Information);
                    return;
                }
                TxtToken.Password = File.ReadAllText(saved).Trim();
                LabHint.Text = "已填入上次保存的令牌。";
            }
            catch (Exception ex)
            {
                MessageBox.Show(this, "读不出来：" + ex.Message,
                    "创作者工具", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
        }

        private void BtnCopyShare_Click(object sender, RoutedEventArgs e)
        {
            if (_current == null) return;
            var text = "我用 PCV 做了个模块「" + _current.Name + "」\n"
                       + "模块数字：" + _current.Code + "\n"
                       + "在 PCV 的市场里切到 GitHub，搜「" + _current.Name + "」就能装。";
            try
            {
                Clipboard.SetText(text);
                LabHint.Text = "分享文案已复制。";
            }
            catch { LabHint.Text = "剪贴板被占着，再点一次试试。"; }
        }

        private void BtnGoMarket_Click(object sender, RoutedEventArgs e)
        {
            if (Owner is MainWindow main) main.ShowMarketForCreator();
            Close();
        }

        private void TryRestoreRepo()
        {
            try
            {
                if (Owner is MainWindow main && main.Settings != null)
                    TxtRepo.Text = main.Settings.GetValue(RepoKey, "");
            }
            catch { }
        }

        private void RememberRepo(string repo)
        {
            try
            {
                if (Owner is MainWindow main && main.Settings != null)
                {
                    main.Settings.SetValue(RepoKey, repo);
                    main.Settings.Save();
                }
            }
            catch { }
        }

        // ── 窗口操作 ────────────────────────────────────────────────────────

        private void TitleBar_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            if (e.ButtonState == MouseButtonState.Pressed) DragMove();
        }

        private void BtnClose_Click(object sender, RoutedEventArgs e) => Close();
    }

    /// <summary>
    /// 已读出来的模块，上传时用它算清单。
    /// 校验复用安装器的 Inspect：能装才让它发，不另写一套规则。
    /// </summary>
    internal sealed class CreatorPackage
    {
        public string PackagePath { get; private set; }
        public string Id { get; private set; }
        public string Name { get; private set; }
        public string Version { get; private set; }
        public string Summary { get; private set; }
        public string Category { get; private set; }
        public string IconText { get; private set; }
        public string Accent { get; private set; }
        public long Size { get; private set; }
        public string Sha256 { get; private set; }
        public string Code { get; private set; }
        public string FileName { get; private set; }
        public int EntryCount { get; private set; }
        public bool Ok { get; private set; }
        public string Problem { get; private set; }

        public static CreatorPackage Inspect(string path)
        {
            var result = new CreatorPackage { PackagePath = path };
            var installer = new PackageInstaller(Path.Combine(Path.GetTempPath(), "pcv-creator"),
                new SilentLogger());
            // 发布前检查不要求依赖已装：创作者机器上不一定装了前置
            var inspection = installer.Inspect(path, requireInstalledDependencies: false);

            if (!inspection.IsValid || inspection.Manifest == null)
            {
                result.Ok = false;
                result.Problem = string.Join("；", inspection.Errors);
                return result;
            }

            var manifest = inspection.Manifest;
            result.Id = manifest.Id;
            result.Name = manifest.DisplayName;
            result.Version = manifest.Version;
            result.Summary = manifest.Description ?? "";
            result.Category = manifest.CategoryLabel;
            result.IconText = manifest.Icon;
            result.Accent = manifest.Accent;
            // 注意用「包文件本身的大小」，不是 inspection.TotalBytes。
            // TotalBytes 是**解压后**的总字节数（给「解压会不会太大」那条检查用的），
            // 而市场下载后比的是 `.pclpkg` 文件大小。用错就会写出一个永远对不上的
            // size，别人一装就报「大小和清单对不上」——这个坑真踩过。
            result.Size = new FileInfo(path).Length;
            result.Sha256 = inspection.Sha256;
            result.EntryCount = inspection.EntryCount;
            result.FileName = manifest.Id + "-" + manifest.Version + ".pclpkg";
            result.Code = ModuleCode.Compute(manifest.Id, manifest.Version, inspection.Sha256);
            result.Ok = true;
            return result;
        }

        /// <summary>写进仓库根目录的 pcv-module.json。字段与 GitHubSource.ManualDocument 对应。</summary>
        public string ManifestJson(string repo)
        {
            var payload = new
            {
                schema = 1,
                id = Id,
                name = Name,
                version = Version,
                author = repo.Split('/')[0],
                summary = Summary,
                category = Category,
                icon = IconText,
                accent = Accent,
                file = FileName,
                size = Size,
                sha256 = Sha256,
                moduleCode = Code,
                generator = "PCV Creator 1",
            };
            return JsonConvert.SerializeObject(payload, Formatting.Indented);
        }
    }

    /// <summary>上传失败时能说人话的异常。</summary>
    internal sealed class GitHubException : Exception
    {
        public GitHubException(string friendly, Exception inner = null) : base(friendly, inner)
            => Friendly = friendly;

        public string Friendly { get; }
    }

    /// <summary>
    /// 往 GitHub 仓库发一个模块：传包 → 写清单 → 打 topic。
    ///
    /// 用 Contents API 而不是 git push：用户机器上大概没有配 git，
    /// 而且「一个文件一次提交」正好是这个场景需要的粒度。
    /// </summary>
    internal sealed class GitHubPublisher
    {
        private const string ApiRoot = "https://api.github.com";
        private readonly string _token;

        public GitHubPublisher(string token) => _token = token;

        public sealed class Result
        {
            public string Repo { get; set; }
            public string Branch { get; set; }
            public bool Updated { get; set; }
            public List<string> Files { get; } = new List<string>();
            /// <summary>这次顺手清掉的旧版包。</summary>
            public List<string> Removed { get; } = new List<string>();
        }

        public async Task<Result> PublishAsync(CreatorPackage package, string repo, string branch,
            IProgress<string> progress)
        {
            var result = new Result { Repo = repo, Branch = branch };

            progress?.Report("正在检查仓库…");
            var repoInfo = await GetRepoAsync(repo).ConfigureAwait(true);
            if (repoInfo == null)
                throw new GitHubException("找不到仓库 " + repo + "。先在 GitHub 上建一个空仓库再来。");
            if (string.IsNullOrWhiteSpace(repoInfo.DefaultBranch)) repoInfo.DefaultBranch = branch;

            // 1) 传包
            progress?.Report("正在上传 " + package.FileName + "…");
            var bytes = File.ReadAllBytes(package.PackagePath);
            if (bytes.Length > 25 * 1024 * 1024)
                throw new GitHubException("包超过 25 MB，Contents API 传不了。"
                                          + "把包压小一点，或者手动用 git 推上去。");
            await PutFileAsync(repo, repoInfo.DefaultBranch, package.FileName, bytes,
                "添加模块包 " + package.FileName).ConfigureAwait(true);
            result.Files.Add(package.FileName);

            // 2) 写清单
            progress?.Report("正在写 " + GitHubSource.ManifestName + "…");
            var manifest = Encoding.UTF8.GetBytes(package.ManifestJson(repo));
            var updated = await PutFileAsync(repo, repoInfo.DefaultBranch, GitHubSource.ManifestName, manifest,
                "更新模块清单 " + package.Id + " " + package.Version).ConfigureAwait(true);
            result.Updated = updated;
            result.Files.Add(GitHubSource.ManifestName);

            // 3) 清掉同一个模块的旧版包
            //
            // 不清的话，每发一版就在仓库里留一个 .pclpkg：占空间、`git clone` 越来越大，
            // 而且别人看到一堆文件不知道哪个才是当前的（清单只指向最新的那份）。
            // 只删「同 id 的其他版本」，别的模块的文件一根手指都不碰。
            progress?.Report("正在清理旧版包…");
            var removed = await RemoveOldPackagesAsync(repo, repoInfo.DefaultBranch, package).ConfigureAwait(true);
            result.Removed.AddRange(removed);

            // 4) 打 topic
            progress?.Report("正在给仓库打 pcv-module 标记…");
            await PutTopicsAsync(repo).ConfigureAwait(true);

            progress?.Report("发好了");
            return result;
        }

        /// <summary>
        /// 删掉同一模块的旧版本包（<id>-<旧版本>.pclpkg）。
        ///
        /// 判据是「文件名以 `id-` 开头、以 `.pclpkg` 结尾、且不是这次发的那个」。
        /// 为什么按文件名而不按清单：清单只记当前版本，历史文件根本读不到版本号。
        /// 删错了代价很大（把别人的包删了），所以前缀必须精确到 `id-`，
        /// 而且跳过所有不属于这个 id 的文件。
        /// </summary>
        private async Task<List<string>> RemoveOldPackagesAsync(string repo, string branch, CreatorPackage package)
        {
            var removed = new List<string>();
            var prefix = package.Id + "-";
            var url = ApiRoot + "/repos/" + repo + "/contents/?ref=" + Uri.EscapeDataString(branch);

            List<ContentEntry> entries;
            try
            {
                var json = await SendAsync("GET", url, null).ConfigureAwait(true);
                entries = JsonConvert.DeserializeObject<List<ContentEntry>>(json) ?? new List<ContentEntry>();
            }
            catch (GitHubException) { return removed; }

            foreach (var entry in entries)
            {
                if (entry == null || string.IsNullOrWhiteSpace(entry.Name) || string.IsNullOrWhiteSpace(entry.Sha))
                    continue;
                if (!entry.Name.EndsWith(".pclpkg", StringComparison.OrdinalIgnoreCase)) continue;
                if (!entry.Name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) continue;
                if (string.Equals(entry.Name, package.FileName, StringComparison.OrdinalIgnoreCase)) continue;

                try
                {
                    await SendAsync("DELETE", ApiRoot + "/repos/" + repo + "/contents/"
                        + Uri.EscapeDataString(entry.Name),
                        JsonConvert.SerializeObject(new
                        {
                            message = "删除旧版包 " + entry.Name + "（当前版本 " + package.Version + "）",
                            sha = entry.Sha,
                            branch,
                        })).ConfigureAwait(true);
                    removed.Add(entry.Name);
                }
                catch (GitHubException)
                {
                    // 旧包删不掉不影响这次发布：清单已经指向新包了。
                    // 但要在界面上说一声，不然仓库会越攒越肥还没人知道。
                }
            }
            return removed;
        }

        // ── HTTP ───────────────────────────────────────────────────────────

        private sealed class RepoDocument
        {
            [JsonProperty("default_branch")] public string DefaultBranch { get; set; }
            [JsonProperty("name")] public string Name { get; set; }
            [JsonProperty("full_name")] public string FullName { get; set; }
        }

        private sealed class TopicsDocument
        {
            [JsonProperty("names")] public List<string> Names { get; set; } = new List<string>();
        }

        private async Task<RepoDocument> GetRepoAsync(string repo)
        {
            try
            {
                var json = await SendAsync("GET", ApiRoot + "/repos/" + repo, null).ConfigureAwait(true);
                return JsonConvert.DeserializeObject<RepoDocument>(json);
            }
            catch (GitHubException) { return null; }
        }

        /// <summary>放一个文件。已存在就带 sha 覆盖。返回是否是「更新」（而不是新建）。</summary>
        private async Task<bool> PutFileAsync(string repo, string branch, string path, byte[] content, string message)
        {
            var url = ApiRoot + "/repos/" + repo + "/contents/" + Uri.EscapeDataString(path);
            string existingSha = null;
            try
            {
                var json = await SendAsync("GET", url + "?ref=" + Uri.EscapeDataString(branch), null).ConfigureAwait(true);
                var doc = JsonConvert.DeserializeObject<ContentDocument>(json);
                existingSha = doc?.Sha;
            }
            catch (GitHubException) { /* 不存在就是新建 */ }

            var payload = new Dictionary<string, object>
            {
                ["message"] = message,
                ["content"] = Convert.ToBase64String(content),
                ["branch"] = branch,
            };
            if (!string.IsNullOrEmpty(existingSha)) payload["sha"] = existingSha;

            await SendAsync("PUT", url, JsonConvert.SerializeObject(payload)).ConfigureAwait(true);
            return !string.IsNullOrEmpty(existingSha);
        }

        private async Task PutTopicsAsync(string repo)
        {
            // 先把现有的 topic 读出来，别把人家原有的冲掉
            var names = new List<string>();
            try
            {
                var json = await SendAsync("GET", ApiRoot + "/repos/" + repo + "/topics", null).ConfigureAwait(true);
                var doc = JsonConvert.DeserializeObject<TopicsDocument>(json);
                if (doc?.Names != null) names.AddRange(doc.Names);
            }
            catch (GitHubException) { }

            if (!names.Any(n => string.Equals(n, GitHubSource.Topic, StringComparison.OrdinalIgnoreCase)))
                names.Add(GitHubSource.Topic);

            await SendAsync("PUT", ApiRoot + "/repos/" + repo + "/topics",
                JsonConvert.SerializeObject(new { names })).ConfigureAwait(true);
        }

        private sealed class ContentDocument
        {
            [JsonProperty("sha")] public string Sha { get; set; }
        }

        /// <summary>列目录时的一条。只取用得到的字段。</summary>
        private sealed class ContentEntry
        {
            [JsonProperty("name")] public string Name { get; set; }
            [JsonProperty("sha")] public string Sha { get; set; }
            [JsonProperty("type")] public string Type { get; set; }
        }

        private async Task<string> SendAsync(string method, string url, string body)
        {
            return await Task.Run(() =>
            {
                try
                {
                    ServicePointManager.SecurityProtocol |= SecurityProtocolType.Tls12;
                    var request = (HttpWebRequest)WebRequest.Create(url);
                    request.Method = method;
                    request.Timeout = 20000;
                    request.ReadWriteTimeout = 20000;
                    request.UserAgent = "PCV-Creator/1";
                    request.Accept = "application/vnd.github+json";
                    request.Headers["Authorization"] = "Bearer " + _token;
                    request.Headers["X-GitHub-Api-Version"] = "2022-11-28";

                    if (body != null)
                    {
                        var bytes = Encoding.UTF8.GetBytes(body);
                        request.ContentType = "application/json";
                        request.ContentLength = bytes.Length;
                        using (var stream = request.GetRequestStream()) stream.Write(bytes, 0, bytes.Length);
                    }

                    using (var response = (HttpWebResponse)request.GetResponse())
                    using (var stream = response.GetResponseStream())
                    using (var reader = new StreamReader(stream, Encoding.UTF8))
                        return reader.ReadToEnd();
                }
                catch (WebException ex)
                {
                    throw Translate(ex);
                }
            }).ConfigureAwait(true);
        }

        private static GitHubException Translate(WebException error)
        {
            var status = 0;
            string detail = "";
            if (error.Response is HttpWebResponse response)
            {
                status = (int)response.StatusCode;
                try
                {
                    using (var stream = response.GetResponseStream())
                    using (var reader = new StreamReader(stream, Encoding.UTF8))
                        detail = reader.ReadToEnd();
                }
                catch { }
            }

            string friendly;
            switch (status)
            {
                case 401:
                    friendly = "令牌无效或已过期。重新建一个 classic token（勾 repo）。";
                    break;
                case 403:
                    friendly = "这个令牌没有写这个仓库的权限。classic token 要勾上 repo；"
                               + "细粒度令牌要给它 Contents 和 Administration 的写权限。";
                    break;
                case 404:
                    friendly = "找不到仓库。确认 owner/name 写对了、仓库已经建好、"
                               + "而且令牌的主人就是这个仓库的所有者。";
                    break;
                case 422:
                    friendly = "GitHub 拒了这次改动。多半是分支名不对（新仓库默认 main 或 master），"
                               + "或者同一个文件正在被别的提交改。";
                    break;
                default:
                    friendly = "GitHub 返回 " + status + "。"
                               + (string.IsNullOrWhiteSpace(detail) ? error.Message : Shorten(detail));
                    break;
            }
            return new GitHubException(friendly, error);
        }

        private static string Shorten(string text)
        {
            var one = text.Replace("\r", " ").Replace("\n", " ");
            return one.Length > 140 ? one.Substring(0, 140) + "…" : one;
        }
    }

    /// <summary>创作者工具里不需要日志，但安装器要一个。</summary>
    internal sealed class SilentLogger : Pcv.Modular.Abstractions.ILogger
    {
        public void Info(string message) { }
        public void Warn(string message) { }
        public void Error(string message, Exception exception = null) { }
        public void Debug(string message) { }
    }
}
