using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using Pcv.Modular.Abstractions;
using Pcv.Modular.Contracts;
using Pcv.Modular.Host;
using Pcv.Modular.Packages;

namespace Pcv.Shell
{
    /// <summary>
    /// 六页：桌面 / 安装 / 框架 / 市场 / 版权 / 新手引导，左侧导航切换。
    /// 自检在独立窗口里（侧栏底部），不占导航项 —— 它是一次性结果，不是一页内容。
    /// 初次启动会停在引导页；之后一律默认回桌面。
    /// </summary>
    public partial class MainWindow : Window
    {
        private const string DeepSeekUrl = "https://chat.deepseek.com/";
        private const string OnboardingKey = "OnboardingDone";

        private readonly ModuleHost _host;
        private readonly ModuleHostAccessor _accessor;
        private readonly PackageInstaller _installer;
        private readonly ObservableCollection<FrameworkCard> _frameworks = new ObservableCollection<FrameworkCard>();
        private readonly ObservableCollection<FrameworkCard> _software = new ObservableCollection<FrameworkCard>();
        private readonly ObservableCollection<FrameworkCard> _desktop = new ObservableCollection<FrameworkCard>();
        private readonly ObservableCollection<MarketCard> _market = new ObservableCollection<MarketCard>();
        private readonly ObservableCollection<EndpointCard> _endpoints = new ObservableCollection<EndpointCard>();
        private readonly MarketService _marketService;
        private readonly ISettingsService _settings;
        private readonly AdminService _admin;
        private readonly UpdateService _updates;
        private readonly IApiGateway _gateway;
        private readonly IAdminKeyProvider _adminKey;
        private ModuleLoadReport _report;
        private MarketCatalog _catalog;
        private bool _marketLoading;
        private bool _iconsMode = true;
        private bool _adminUnlocked;
        private bool _adminBusy;
        private string _unlockKey = "";

        /// <summary>市场当前看的是哪一边：官方（博客）还是 GitHub（社区）。</summary>
        private MarketSource _marketSource = MarketSource.Official;

        private enum MarketSource { Official, GitHub }

        /// <summary>创作者工具要读写本地设置，走这个。</summary>
        public ISettingsService Settings => _settings;

        public ModuleLoadReport CurrentReport => _report;

        public MainWindow(ModuleHost host, ModuleLoadReport report)
        {
            _host = host;
            _report = report;
            InitializeComponent();

            _installer = new PackageInstaller(host.ModulesDirectory, host.Log);
            _accessor = new ModuleHostAccessor(host);
            host.Services.TryGet(out _settings);
            host.Services.TryGet(out _gateway);
            host.Services.TryGet(out _adminKey);
            _marketService = new MarketService(
                Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "PCLData"));
            _admin = new AdminService(_marketService, _settings, _gateway);
            _updates = new UpdateService(host.Host.Version, _gateway);

            ListFrameworks.ItemsSource = _frameworks;
            ListSoftware.ItemsSource = _software;
            GridDesktop.ItemsSource = _desktop;
            ListMarket.ItemsSource = _market;
            ListAdminEndpoints.ItemsSource = _endpoints;
            LabVersion.Text = "PCV " + host.Host.Version;
            LabMarketSource.Text = "服务端：" + MarketService.MarketUrl;
            TxtAdminAccessKey.Text = _gateway?.AccessKey ?? "";

            TrmThemeService.ThemeChanged += OnTrmThemeChanged;
            if (_gateway != null) _gateway.Changed += _ => OnUi(RefreshEndpoints);
            if (_adminKey != null) _adminKey.KeyChanged += _ => OnUi(OnAdminKeyChanged);
            RefreshAdminKeyView();
            RefreshEndpoints();
            SyncMarketToggle();
            RefreshThemeButtons();

            RefreshModules();
            ApplyViewMode();
            ShowView(IsOnboardingDone() ? "home" : "onboarding");

            Loaded += async (s, e) =>
            {
                GlassHelper.TryEnableRoundedCorners(this);
                // 启动就悄悄问一次：有新版本才在桌面顶上挂一条提示，不打扰、不下载。
                await CheckUpdateAsync(false);
            };
        }

        // ── 新手引导 ────────────────────────────────────────────────────────

        private bool IsOnboardingDone() => _settings != null && _settings.GetValue(OnboardingKey, false);

        private void MarkOnboardingDone()
        {
            if (_settings == null) return;
            _settings.SetValue(OnboardingKey, true);
            _settings.Save();
        }

        private void BtnOnboarding_Click(object sender, RoutedEventArgs e) => ShowView("onboarding");

        private void BtnOnboardingDone_Click(object sender, RoutedEventArgs e)
        {
            MarkOnboardingDone();
            ShowView("home");
            SetStatus("引导看完了，桌面在这里");
        }

        /// <summary>给命令行截图用：--page onboarding 会直接停在这页。</summary>
        public void ForceOnboarding() => ShowView("onboarding");

        // ── 列表 ────────────────────────────────────────────────────────────

        private void RefreshModules()
        {
            _frameworks.Clear();
            _software.Clear();
            _desktop.Clear();
            PanExtraCategories.Children.Clear();

            var loaded = _report.Entries.ToDictionary(e => e.Id, e => e, StringComparer.OrdinalIgnoreCase);
            var extras = new Dictionary<string, ObservableCollection<FrameworkCard>>(StringComparer.OrdinalIgnoreCase);

            foreach (var installed in _installer.ListInstalled())
            {
                loaded.TryGetValue(installed.Id, out var entry);
                var manifest = ModuleHostAccessor.ReadManifest(installed.Directory);
                var isProcess = string.Equals(manifest?.Kind, "process", StringComparison.OrdinalIgnoreCase);
                var isSoftware = manifest != null && manifest.IsSoftware;
                var isBase = manifest != null && manifest.IsBaseFramework;
                var label = manifest?.CategoryLabel ?? "框架";

                string state;
                if (entry != null) state = StateText(entry.State);
                else if (isProcess) state = _accessor.IsRunning(installed.Id) ? "运行中" : "未运行";
                else state = "未加载";

                var detail = entry?.Error ?? manifest?.Description ?? (isProcess ? "独立进程框架" : "自带");

                var hasSurface = entry?.Instance is Pcv.Modular.Contracts.IFrameworkSurface;
                var noSurface = entry != null && !isProcess && !hasSurface && isBase == false && isSoftware == false;

                var card = new FrameworkCard
                {
                    Id = installed.Id,
                    Name = entry?.Manifest?.DisplayName ?? manifest?.DisplayName ?? installed.Name,
                    Version = entry?.Manifest?.Version ?? manifest?.Version ?? installed.Version,
                    State = state,
                    Detail = noSurface ? "⚠ 没有提供界面（不符合分类要求）· " + detail : detail,
                    Initial = Initial(installed.Id),
                    CanUninstall = !string.Equals(installed.Id, "pcv.core", StringComparison.OrdinalIgnoreCase),
                    CanOpen = !isBase,
                    IsBase = isBase,
                    BaseLabel = isBase ? (manifest.BaseFor + " 的基础框架") : "",
                    Category = isSoftware ? "software" : "framework",
                    CategoryLabel = label,
                    Icon = manifest?.Icon,
                    Accent = manifest?.Accent,
                    Order = manifest?.Order ?? 100,
                    Desktop = manifest?.Desktop ?? true
                };

                // 桌面：所有声明了 desktop 且能打开的东西都上桌，不只软件。
                // 基础框架是"被用的一方"，摆在桌面上只会让人点开一片空白。
                if (card.Desktop && card.CanOpen)
                    _desktop.Add(card);

                if (isSoftware)
                {
                    _software.Add(card);
                }
                else if (manifest != null && !manifest.IsBuiltInCategory)
                {
                    // 自定义类别：单独一桶，等下生成分组
                    if (!extras.TryGetValue(label, out var bucket))
                    {
                        bucket = new ObservableCollection<FrameworkCard>();
                        extras[label] = bucket;
                    }
                    bucket.Add(card);
                }
                else
                {
                    _frameworks.Add(card);
                }
            }

            // 桌面按清单里的 order 排，其次按名字；新增/卸载后位置稳定
            var ordered = _desktop.OrderBy(c => c.Order).ThenBy(c => c.Name, StringComparer.CurrentCulture).ToList();
            _desktop.Clear();
            foreach (var card in ordered) _desktop.Add(card);

            foreach (var pair in extras.OrderBy(p => p.Key, StringComparer.OrdinalIgnoreCase))
                PanExtraCategories.Children.Add(BuildCategoryGroup(pair.Key, pair.Value));

            var total = _frameworks.Count + extras.Values.Sum(list => list.Count);
            LabFrameworkTitle.Text = $"共 {total} 个 · 提供能力，被软件调用";
            LabSoftwareCount.Text = _desktop.Count + " 个应用 · 点图标打开";
            LabNoSoftware.Visibility = _desktop.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
            LabNoFramework.Visibility = total == 0 ? Visibility.Visible : Visibility.Collapsed;
            if (_catalog != null) RefreshMarketStates();
        }

        /// <summary>给 IHostControl 用：重新加载模块并刷新所有列表。</summary>
        public void RefreshFromHost()
        {
            _report = _host.ReloadAll();
            RefreshModules();
        }

        /// <summary>给 IHostControl 用：在状态栏说一句话。</summary>
        public void SetHostStatus(string message, bool isError) => SetStatus(message, isError);

        /// <summary>给 IHostControl 用：切页时把窗口带到前面。</summary>
        public void BringToFront()
        {
            if (!IsVisible) Show();
            if (WindowState == WindowState.Minimized) WindowState = WindowState.Normal;
            Activate();
        }

        private void BtnViewMode_Click(object sender, RoutedEventArgs e)
        {
            if (sender is FrameworkElement element && element.Tag is string mode)
            {
                _iconsMode = mode == "icons";
                ApplyViewMode();
            }
        }

        private void ApplyViewMode()
        {
            GridDesktop.Visibility = _iconsMode ? Visibility.Visible : Visibility.Collapsed;
            ListSoftware.Visibility = _iconsMode ? Visibility.Collapsed : Visibility.Visible;
            Use(BtnViewIcons, TextElement.ForegroundProperty, _iconsMode ? "TrmAccent" : "TrmInkMuted");
            Use(BtnViewList, TextElement.ForegroundProperty, _iconsMode ? "TrmInkMuted" : "TrmAccent");
        }

        private void BtnOpenMarket_Click(object sender, RoutedEventArgs e) => ShowView("market");

        /// <summary>给自定义分类生成一组：小标题 + 卡片列表。</summary>
        private FrameworkElement BuildCategoryGroup(string label, ObservableCollection<FrameworkCard> cards)
        {
            var template = (DataTemplate)FindResource("FrameworkCardTemplate");

            var panel = new StackPanel { Margin = new Thickness(0, 20, 0, 0) };
            var heading = new TextBlock
            {
                Text = label + "（" + cards.Count + " 个）",
                FontSize = 11.5,
                FontWeight = FontWeights.SemiBold,
                Margin = new Thickness(2, 0, 0, 8)
            };
            Use(heading, TextElement.ForegroundProperty, "TrmMeta");
            panel.Children.Add(heading);
            panel.Children.Add(new ItemsControl { ItemsSource = cards, ItemTemplate = template });
            return panel;
        }

        private ObservableCollection<FrameworkCard> _cardsTarget(bool isSoftware)
            => isSoftware ? _software : _frameworks;

        /// <summary>
        /// 两个字母的缩写：pcl.core → Co，pcv.cleaner → Cl。
        /// 只取一个字母的话，core 和 cleaner 会撞成同一个 C，看着像 bug。
        /// </summary>
        private static string Initial(string moduleId)
        {
            if (string.IsNullOrWhiteSpace(moduleId)) return "?";
            var tail = (moduleId.Split('.').LastOrDefault() ?? moduleId).Trim();
            if (tail.Length == 0) return "?";
            if (tail.Length == 1) return tail.ToUpperInvariant();
            return char.ToUpperInvariant(tail[0]).ToString() + tail[1];
        }

        private static string StateText(ModuleState state)
        {
            switch (state)
            {
                case ModuleState.Started: return "已完成安装";
                case ModuleState.Loaded: return "已安装";
                case ModuleState.Stopped: return "停了";
                case ModuleState.Skipped: return "跳过";
                case ModuleState.Failed: return "没装好";
                default: return state.ToString();
            }
        }

        /// <summary>运行时会换主题的状态色必须绑到资源引用，一次性取 Brush 会留在旧主题里。</summary>
        private static void Use(FrameworkElement element, DependencyProperty property, string key)
            => element.SetResourceReference(property, key);

        private void OnTrmThemeChanged(bool dark)
        {
            // 卡片状态色是绑定 getter 取的一次性 Brush，换主题后重建一次列表即可
            RefreshModules();
            RefreshEndpoints();
            ApplyViewMode();
            RefreshThemeButtons();
        }

        private void SetStatus(string message, bool isError = false)
        {
            LabStatus.Text = message;
            Use(LabStatus, TextElement.ForegroundProperty, isError ? "TrmDanger" : "TrmInkMuted");
        }

        // ── 两页切换 ────────────────────────────────────────────────────────

        private void ShowHome()
        {
            ViewAdd.Visibility = Visibility.Collapsed;
            ViewHome.Visibility = Visibility.Visible;
            SetStatus("好了");
        }

        private void BtnOpenAdd_Click(object sender, RoutedEventArgs e)
        {
            ViewHome.Visibility = Visibility.Collapsed;
            ViewAdd.Visibility = Visibility.Visible;
            SetStatus("挑一条：装现成的，或者自己做一个");
        }

        private void BtnBack_Click(object sender, RoutedEventArgs e) => ShowHome();

        /// <summary>用户视角的体检：现在装的这套东西健不健康。一次性结果，所以不占导航项。</summary>
        private void BtnSelfCheck_Click(object sender, RoutedEventArgs e)
        {
            new SelfCheckWindow(_report, _host.ModulesDirectory,
                                AppDomain.CurrentDomain.BaseDirectory,
                                _host.Host.Version) { Owner = this }.ShowDialog();
        }

        /// <summary>给 App 的自动启动用：不带 owner，窗口就是独立窗口。</summary>
        public void RunProcessFrameworkFor(ModuleManifest manifest, string directory)
        {
            _accessor.RunProcessFramework(manifest, directory, null);
        }

        /// <summary>
        /// 打开一个框架。两种形态走两条路：
        ///   · 进程型（py / node / exe）→ 起子进程，给它一个独立的深色窗口；
        ///   · 程序集型（.NET DLL）→ 用宿主进程里的实例，弹 FrameworkWindow。
        /// 命令行 --open 和界面上的按钮都从这里走，保证只有一套逻辑。
        /// </summary>
        public void OpenModuleById(string moduleId)
        {
            var directory = Path.Combine(_installer.ModulesDirectory, moduleId);
            var manifest = ModuleHostAccessor.ReadManifest(directory);
            var isProcess = string.Equals(manifest?.Kind, "process", StringComparison.OrdinalIgnoreCase);

            if (isProcess)
            {
                try
                {
                    _accessor.RunProcessFramework(manifest, directory, this);
                    SetStatus($"{manifest.DisplayName} 起来了，窗口是它自己的");
                    RefreshModules();
                }
                catch (Exception ex)
                {
                    SetStatus("启动失败：" + ex.Message, true);
                    _host.Log.Error($"启动 {moduleId} 失败", ex);
                }
                return;
            }

            var entry = _report.Entries.FirstOrDefault(m => string.Equals(m.Id, moduleId, StringComparison.OrdinalIgnoreCase));
            if (entry == null)
            {
                SetStatus($"没有 {moduleId} 这个框架", true);
                return;
            }
            if (entry.State != ModuleState.Started && entry.State != ModuleState.Loaded)
            {
                SetStatus($"{moduleId} 现在打不开：{entry.Error ?? StateText(entry.State)}", true);
                return;
            }

            try
            {
                new FrameworkWindow(entry) { Owner = this, ShowInTaskbar = true }.Show();
            }
            catch (Exception ex)
            {
                SetStatus("打开失败：" + ex.Message, true);
            }
        }

        /// <summary>渲染"展开框架"那一帧用。</summary>
        public void ExpandFrameworksForRender()
        {
            ShowView("frameworks");
        }

        /// <summary>离屏渲染时直接切到第二页，省得为了截图去模拟点击。</summary>
        /// <summary>四页导航：home / install / frameworks / credits。</summary>
        /// <summary>右下角那行"基于 PCL 的 PCV"，点了去版权页看细节。</summary>
        private void Footer_Click(object sender, MouseButtonEventArgs e) => ShowView("credits");

        private void Nav_Click(object sender, RoutedEventArgs e)
        {
            if (sender is FrameworkElement element && element.Tag is string view)
                ShowView(view);
        }

        private void ShowView(string view)
        {
            ViewHome.Visibility = view == "home" ? Visibility.Visible : Visibility.Collapsed;
            ViewAdd.Visibility = view == "install" ? Visibility.Visible : Visibility.Collapsed;
            ViewFrameworks.Visibility = view == "frameworks" ? Visibility.Visible : Visibility.Collapsed;
            ViewMarket.Visibility = view == "market" ? Visibility.Visible : Visibility.Collapsed;
            ViewApi.Visibility = view == "api" ? Visibility.Visible : Visibility.Collapsed;
            ViewOnboarding.Visibility = view == "onboarding" ? Visibility.Visible : Visibility.Collapsed;
            ViewCredits.Visibility = view == "credits" ? Visibility.Visible : Visibility.Collapsed;

            Highlight(NavSoftware, view == "home");
            Highlight(NavInstall, view == "install");
            Highlight(NavFrameworks, view == "frameworks");
            Highlight(NavMarket, view == "market");
            Highlight(NavCredits, view == "credits");

            // API 页不是导航项，高亮全部清掉即可 —— 它本来就不该在侧栏发光
            if (view == "api") SetStatus("API");

            if (view == "frameworks") RefreshModules();
            if (view == "market") LoadMarketAsync();
            if (view == "api") { RefreshAdminKeyView(); RefreshEndpoints(); SyncMarketToggle(); }
        }

        // ── API 页的分段切换 ────────────────────────────────────────────────
        // 原来市场、更新、接口、外观是四张卡竖着堆，一屏看不到底。
        // 现在按「状态 / 市场 / 接口 / 更新」分四段，顶上切换，下面只显示一段。
        // 分段只是为了不再拉长页面，权限没变：没解锁时整块 PanAdminTools 都是禁用的。

        private void Seg_Click(object sender, RoutedEventArgs e)
        {
            if (sender is FrameworkElement element && element.Tag is string segment)
                ShowApiSegment(segment);
        }

        private void ShowApiSegment(string segment)
        {
            SegOverview.Visibility = segment == "overview" ? Visibility.Visible : Visibility.Collapsed;
            SegMarket.Visibility = segment == "market" ? Visibility.Visible : Visibility.Collapsed;
            SegEndpoints.Visibility = segment == "endpoints" ? Visibility.Visible : Visibility.Collapsed;
            SegUpdate.Visibility = segment == "update" ? Visibility.Visible : Visibility.Collapsed;

            SetSegmentButton(BtnSegOverview, segment == "overview");
            SetSegmentButton(BtnSegMarket, segment == "market");
            SetSegmentButton(BtnSegEndpoints, segment == "endpoints");
            SetSegmentButton(BtnSegUpdate, segment == "update");
        }

        private void SetSegmentButton(System.Windows.Controls.Button button, bool active)
        {
            if (button == null) return;
            button.Style = (Style)FindResource(active ? "PrimaryButton" : "GhostButton");
        }

        // ── 市场 ────────────────────────────────────────────────────────────

        private async void LoadMarketAsync()
        {
            if (_marketLoading) return;
            _marketLoading = true;
            LabMarketStatus.Text = "正在读取市场…";
            Use(PanMarketStatus, Border.BackgroundProperty, "TrmAccentSoft");
            Use(PanMarketStatus, Border.BorderBrushProperty, "TrmLine");
            Use(LabMarketStatus, TextElement.ForegroundProperty, "TrmInkMuted");

            try
            {
                var result = await _marketService.LoadAsync(true);
                ApplyMarketResult(result);
            }
            catch (Exception ex)
            {
                LabMarketStatus.Text = "读取市场出错：" + ex.Message;
                SetMarketWarning();
            }
            finally
            {
                _marketLoading = false;
            }
        }

        /// <summary>异步加载与截图用的同步加载共用这一份渲染逻辑。</summary>
        private void ApplyMarketResult(MarketResult result)
        {
            _catalog = result.Catalog;
            if (!result.Success)
            {
                LabMarketStatus.Text = result.Message ?? "市场暂时打不开。";
                SetMarketWarning();
                _market.Clear();
                LabNoMarket.Visibility = Visibility.Visible;
                SetStatus("市场没读出来：" + result.Message, true);
                return;
            }

            var offline = !string.Equals(result.Origin, "线上市场", StringComparison.Ordinal);
            var extra = string.IsNullOrEmpty(result.Message) ? "" : " · " + result.Message;
            LabMarketStatus.Text = offline
                ? $"{result.Origin} · {result.Message} 条目能看，安装要等联网。"
                : $"线上市场 · {result.Catalog.Items.Count} 个条目 · 更新于 {result.Catalog.Updated}{extra}";
            if (offline) SetMarketWarning();
            LabMarketSource.Text = "服务端：" + MarketService.MarketUrl
                                   + "　|　清单里的 size / sha256 由 tools/build_market.py 现场生成";
            FillMarket(result.Catalog, result.Origin);
            SetStatus(offline ? "市场用的是本地内容" : "市场已刷新");
        }

        /// <summary>给 --render 截图用：同步拉一次，保证拍到真实条目而不是空列表。</summary>
        public void LoadMarketForRender()
        {
            try { ApplyMarketResult(_marketService.LoadForRender()); }
            catch (Exception ex) { LabMarketStatus.Text = "读取市场出错：" + ex.Message; SetMarketWarning(); }
        }

        /// <summary>
        /// 给 --render --community 用：同步走一遍 GitHub 源，验证社区搜索这条路。
        /// 同步等待是刻意的 —— 截图脚本等不到异步回来。
        /// </summary>
        public void LoadCommunityForRender(string keyword)
        {
            try
            {
                SetMarketSource(MarketSource.GitHub);
                if (!string.IsNullOrWhiteSpace(keyword)) TxtSearch.Text = keyword;

                var items = GitHubSource.SearchAsync(keyword).GetAwaiter().GetResult();
                _market.Clear();
                foreach (var item in items) _market.Add(new MarketCard(item));

                LabMarketStatus.Text = GitHubSource.LastNote;
                if (items.Count == 0) SetMarketWarning();
                LabNoMarket.Visibility = _market.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
                LabMarketSource.Text =
                    "社区来源：GitHub 上带 pcv-module 标记的仓库，每个仓库根目录有一份 "
                    + GitHubSource.ManifestName + "。PCV 不存任何模块文件。";
                RefreshMarketStates();
            }
            catch (Exception ex)
            {
                LabMarketStatus.Text = "去 GitHub 找的时候出错：" + ex.Message;
                SetMarketWarning();
            }
        }

        private void SetMarketWarning()
        {
            Use(PanMarketStatus, Border.BackgroundProperty, "TrmWarnSoft");
            Use(PanMarketStatus, Border.BorderBrushProperty, "TrmWarnSoft");
            Use(LabMarketStatus, TextElement.ForegroundProperty, "TrmWarnInk");
        }

        private void BtnMarketRefresh_Click(object sender, RoutedEventArgs e)
        {
            _market.Clear();
            if (_marketSource == MarketSource.GitHub) LoadGitHubMarketAsync(TxtSearch.Text);
            else LoadMarketAsync();
        }

        // ── 市场来源切换：官方（博客）/ GitHub（社区）──────────────────────
        //
        // 为什么默认官方：社区来源要发 GitHub 搜索请求，配额有限（一分钟十几次），
        // 不该因为用户点进市场就自动消耗。切过去才发。

        private void BtnSourceOfficial_Click(object sender, RoutedEventArgs e)
        {
            SetMarketSource(MarketSource.Official);
            _market.Clear();
            LoadMarketAsync();
        }

        private void BtnSourceGitHub_Click(object sender, RoutedEventArgs e)
        {
            SetMarketSource(MarketSource.GitHub);
            _market.Clear();
            LoadGitHubMarketAsync(TxtSearch.Text);
        }

        private void SetMarketSource(MarketSource source)
        {
            _marketSource = source;
            var github = source == MarketSource.GitHub;

            BtnSourceOfficial.Style = (Style)FindResource(github ? "GhostButton" : "PrimaryButton");
            BtnSourceGitHub.Style = (Style)FindResource(github ? "PrimaryButton" : "GhostButton");
            PanSearch.Visibility = github ? Visibility.Visible : Visibility.Collapsed;
            LabSearchHint.Visibility = string.IsNullOrEmpty(TxtSearch.Text)
                ? Visibility.Visible : Visibility.Collapsed;
        }

        private void TxtSearch_KeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Return)
            {
                _market.Clear();
                LoadGitHubMarketAsync(TxtSearch.Text);
            }
        }

        private void TxtSearch_TextChanged(object sender, TextChangedEventArgs e)
        {
            if (LabSearchHint == null) return;
            LabSearchHint.Visibility = string.IsNullOrEmpty(TxtSearch.Text)
                ? Visibility.Visible : Visibility.Collapsed;
        }

        private async void LoadGitHubMarketAsync(string keyword)
        {
            if (_marketLoading) return;
            _marketLoading = true;
            LabMarketStatus.Text = string.IsNullOrWhiteSpace(keyword)
                ? "正在去 GitHub 上找社区模块…"
                : "正在 GitHub 上搜「" + keyword.Trim() + "」…";
            Use(PanMarketStatus, Border.BackgroundProperty, "TrmAccentSoft");
            Use(PanMarketStatus, Border.BorderBrushProperty, "TrmLine");
            Use(LabMarketStatus, TextElement.ForegroundProperty, "TrmInkMuted");

            try
            {
                var items = await GitHubSource.SearchAsync(keyword);
                _market.Clear();
                foreach (var item in items) _market.Add(new MarketCard(item));

                LabMarketStatus.Text = GitHubSource.LastNote;
                if (items.Count == 0) SetMarketWarning();
                LabNoMarket.Visibility = _market.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
                LabNoMarket.Text = string.IsNullOrWhiteSpace(keyword)
                    ? "GitHub 上暂时没搜到社区模块。"
                    : "没有找到叫「" + keyword.Trim() + "」的模块。换个名字，或者自己做 one 发上去。";
                LabMarketSource.Text =
                    "社区来源：GitHub 上带 pcv-module 标记的仓库，每个仓库根目录有一份 "
                    + GitHubSource.ManifestName + "。PCV 不存任何模块文件。";
                RefreshMarketStates();
                SetStatus("社区模块 " + items.Count + " 个");
            }
            catch (Exception ex)
            {
                LabMarketStatus.Text = "去 GitHub 找的时候出错：" + ex.Message;
                SetMarketWarning();
            }
            finally
            {
                _marketLoading = false;
            }
        }

        private void BtnCreator_Click(object sender, RoutedEventArgs e)
        {
            new CreatorWindow(() => { /* 发完不自动切源，用户自己去市场看 */ })
            { Owner = this, ShowInTaskbar = true }.ShowDialog();
        }

        /// <summary>创作者工具发完包后跳回市场（GitHub 源）。</summary>
        public void ShowMarketForCreator()
        {
            ShowView("market");
            SetMarketSource(MarketSource.GitHub);
            LoadGitHubMarketAsync("");
        }

        private void FillMarket(MarketCatalog catalog, string origin)
        {
            _market.Clear();
            foreach (var item in catalog.Items)
            {
                item.Source = origin;
                item.Offline = !string.Equals(origin, "线上市场", StringComparison.Ordinal);
                _market.Add(new MarketCard(item));
            }
            LabNoMarket.Visibility = _market.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
            RefreshMarketStates();
        }

        /// <summary>按已装版本决定每条显示「安装 / 更新 / 已装」。</summary>
        private void RefreshMarketStates()
        {
            var installed = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            try
            {
                foreach (var module in _installer.ListInstalled())
                    installed[module.Id] = module.Version ?? "";
            }
            catch { /* 读不出来就都当没装 */ }

            foreach (var card in _market)
            {
                installed.TryGetValue(card.Id, out var current);
                card.ApplyInstalled(current);
            }
        }

        private async void BtnMarketInstall_Click(object sender, RoutedEventArgs e)
        {
            if (!(sender is FrameworkElement element) || !(element.Tag is string id)) return;
            var card = _market.FirstOrDefault(c => string.Equals(c.Id, id, StringComparison.OrdinalIgnoreCase));
            if (card == null || !card.CanInstall) return;
            var item = card.Item;

            if (item.Offline)
            {
                SetStatus("现在用的是本地清单，联网后再装。点「刷新」重试。", true);
                return;
            }

            card.CanInstall = false;
            card.ActionText = "下载中";
            SetStatus($"在下载 {item.Name}…");

            try
            {
                var package = await _marketService.DownloadAsync(item);
                var inspection = _installer.Inspect(package);
                if (!inspection.IsValid)
                {
                    SetStatus("包装不了：" + string.Join("；", inspection.Errors.Take(2)), true);
                    card.ActionText = "重试";
                    card.CanInstall = true;
                    return;
                }

                var target = Path.Combine(_installer.ModulesDirectory, inspection.Manifest.Id);
                var overwrite = Directory.Exists(target);
                var result = _installer.Install(package, overwrite);
                if (!result.Success)
                {
                    SetStatus(result.Message, true);
                    card.ActionText = "重试";
                    card.CanInstall = true;
                    return;
                }

                _report = _host.ReloadAll();
                RefreshModules();
                SetStatus($"{item.Name} 装好了，桌面上多了个图标");
            }
            catch (Exception ex)
            {
                SetStatus("安装失败：" + ex.Message, true);
                card.ActionText = "重试";
                card.CanInstall = true;
            }
            finally
            {
                try { System.IO.File.Delete(Path.Combine(Path.GetTempPath(), "pcv-market", item.File)); }
                catch { /* 临时文件删不掉不影响 */ }
            }
        }

        private static void Highlight(System.Windows.Controls.Button button, bool selected)
        {
            if (button == null) return;
            Use(button, Control.BackgroundProperty, selected ? "TrmAccentSoft" : "TrmTransparent");
            Use(button, Control.ForegroundProperty, selected ? "TrmAccent" : "TrmInkMuted");
            button.FontWeight = selected ? FontWeights.SemiBold : FontWeights.Normal;
        }

        /// <summary>版权页里的外链，点了用系统浏览器打开。</summary>
        private void Link_Click(object sender, MouseButtonEventArgs e)
        {
            if (sender is FrameworkElement element && element.Tag is string url)
            {
                try
                {
                    System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(url)
                    {
                        UseShellExecute = true
                    });
                }
                catch (Exception ex)
                {
                    SetStatus("打不开链接：" + ex.Message, true);
                }
            }
        }

        /// <summary>离屏渲染指定页（截图用）。</summary>
        public void ShowViewForRender(string view) => ShowView(view);

        public void ShowAddViewForRender()
        {
            ShowView("install");
            SetStatus("挑一条：装现成的，或者自己做一个");
        }

        // ── 导入 ────────────────────────────────────────────────────────────

        private void BtnChooseFile_Click(object sender, RoutedEventArgs e)
        {
            var dialog = new Microsoft.Win32.OpenFileDialog
            {
                Title = "挑一个框架包",
                Filter = "框架包或构建脚本 (*.pclpkg;*.zip;*.py;*.js;*.ps1;*.bat)|*.pclpkg;*.zip;*.py;*.js;*.ps1;*.bat|所有文件 (*.*)|*.*",
                CheckFileExists = true
            };
            if (dialog.ShowDialog(this) == true) InstallPackage(dialog.FileName);
        }

        // ── 构建脚本：拖进来直接跑，跑完装上 ────────────────────────────────

        private void RunBuildScript(string scriptPath)
        {
            var builder = new ScriptBuilder(AppDomain.CurrentDomain.BaseDirectory, _host.Log);
            ScriptBuilder.Result last = null;

            var window = new BuildWindow(builder, scriptPath, result => last = result) { Owner = this };
            window.InstallRequested += (s, e) =>
            {
                if (last?.PackagePath == null)
                {
                    window.Installed(false, "没找到产物，装不了。");
                    return;
                }

                try
                {
                    var installed = InstallPackageAutomatically(last.PackagePath);
                    window.Installed(installed.Success, installed.Message);
                }
                catch (Exception ex)
                {
                    window.Installed(false, "安装出错：" + ex.Message);
                }
            };
            window.ShowDialog();

            if (last != null && !last.Success)
                SetStatus(last.Message, true);
        }

        // ── 制作：复制提示词 + 打开 DeepSeek ────────────────────────────────

        /// <summary>
        /// 粘贴进来的构建脚本 → 落成临时 .py → 走"拖脚本"同一条构建/安装链路。
        /// 临时文件只是为了让 ScriptBuilder 能当文件读，跑完就删。
        /// </summary>
        private void BuildAndInstallFromCode(string code)
        {
            var file = Path.Combine(Path.GetTempPath(), "pcv-paste-" + Guid.NewGuid().ToString("N").Substring(0, 6) + ".py");
            try
            {
                File.WriteAllText(file, code, new System.Text.UTF8Encoding(false));
                RunBuildScript(file);
            }
            catch (Exception ex)
            {
                SetStatus("构建失败：" + ex.Message, true);
            }
            finally
            {
                try { File.Delete(file); } catch { /* 删不掉就算了 */ }
            }
        }

        private void BtnPasteScript_Click(object sender, RoutedEventArgs e)
        {
            new ScriptWindow(_accessor, BuildAndInstallFromCode) { Owner = this }.ShowDialog();
            RefreshModules();
        }

        private void BtnMake_Click(object sender, RoutedEventArgs e)
        {
            var prompt = GetPrompt();
            var copied = TrySetClipboard(prompt);

            PanCopied.Visibility = Visibility.Visible;
            if (copied)
            {
                LabCopied.Text = $"制作说明复制好了（{prompt.Length} 字）。粘到 DeepSeek 输入框发出去，" +
                                 "它会给你一段 Python 脚本；跑完得到一个 .pclpkg，拖回这个窗口就能装。";
                SetStatus("已复制，DeepSeek 正在打开");
            }
            else
            {
                LabCopied.Text = "剪贴板被别的程序占着，没写进去。再点一次按钮试试。";
                SetStatus("剪贴板没写进去", true);
            }

            BtnMake.Content = "再开一次";
            TryOpenUrl(DeepSeekUrl);
        }

        private string GetPrompt() => PromptLibrary.Load();

        private static bool TrySetClipboard(string text) => ClipboardWriter.TrySetText(text);

        private static void TryOpenUrl(string url)
        {
            try
            {
                Process.Start(url);
            }
            catch (Exception)
            {
                // 没默认浏览器就只留在剪贴板里，不弹错误框打断用户
            }
        }

        // ── 拖放 ────────────────────────────────────────────────────────────

        private static string DroppedFile(DragEventArgs e)
        {
            if (!e.Data.GetDataPresent(DataFormats.FileDrop)) return null;
            var files = e.Data.GetData(DataFormats.FileDrop) as string[];
            if (files == null || files.Length != 1) return null;
            return IsPackagePath(files[0]) || IsScriptPath(files[0]) ? files[0] : null;
        }

        private static bool IsPackagePath(string path)
        {
            var extension = Path.GetExtension(path).ToLowerInvariant();
            return extension == ".pclpkg" || extension == ".zip";
        }

        /// <summary>构建脚本：拖进来直接跑，跑完把产物装上。</summary>
        private static readonly string[] ScriptExtensions =
        {
            ".py", ".js", ".mjs", ".ps1", ".bat", ".cmd", ".lua", ".rb", ".sh"
        };

        private static bool IsScriptPath(string path)
        {
            var extension = Path.GetExtension(path).ToLowerInvariant();
            return Array.IndexOf(ScriptExtensions, extension) >= 0;
        }

        private void Window_DragOver(object sender, DragEventArgs e)
        {
            var dropped = DroppedFile(e);
            var accepted = dropped != null;
            e.Effects = accepted ? DragDropEffects.Copy : DragDropEffects.None;
            e.Handled = true;

            SetDropZoneActive(accepted);
            LabDropTitle.Text = accepted
                ? (IsScriptPath(dropped) ? "松手就构建" : "松手就装")
                : "只认 .pclpkg 和脚本";
        }

        private void Window_DragLeave(object sender, DragEventArgs e) => ResetDropZone();

        private void Window_Drop(object sender, DragEventArgs e)
        {
            var dropped = DroppedFile(e);
            ResetDropZone();
            if (dropped == null) return;

            // 拖到别的页上也算数：先切到"安装"，让人看见进度
            ShowView("install");
            if (IsScriptPath(dropped)) RunBuildScript(dropped);
            else InstallPackage(dropped);
        }

        private void SetDropZoneActive(bool active)
        {
            Use(DropZone, Border.BorderBrushProperty, active ? "TrmAccent" : "TrmLine");
            DropZone.BorderThickness = new Thickness(active ? 2 : 1.5);
            Use(DropZone, Border.BackgroundProperty, active ? "TrmAccentSoft" : "TrmSurface");
        }

        private void ResetDropZone()
        {
            SetDropZoneActive(false);
            LabDropTitle.Text = "拖一个 .pclpkg 或构建脚本进来";
        }

        // ── 安装 / 卸载 ─────────────────────────────────────────────────────

        /// <summary>自动安装：不弹"已装过要不要覆盖"的确认，直接覆盖并重载。</summary>
        private InstallResult InstallPackageAutomatically(string path)
        {
            var inspection = _installer.Inspect(path);
            if (!inspection.IsValid)
                return new InstallResult { Success = false, Message = "包不合规：" + string.Join("；", inspection.Errors.Take(2)) };

            var target = Path.Combine(_installer.ModulesDirectory, inspection.Manifest.Id);
            var result = _installer.Install(path, Directory.Exists(target));

            if (result.Success)
            {
                _report = _host.ReloadAll();
                RefreshModules();
                ShowHome();
            }

            return result;
        }

        private void InstallPackage(string path)
        {
            var inspection = _installer.Inspect(path);
            if (!inspection.IsValid)
            {
                SetStatus("装不了：" + string.Join("；", inspection.Errors.Take(2)), true);
                return;
            }

            var target = Path.Combine(_installer.ModulesDirectory, inspection.Manifest.Id);
            var overwrite = false;
            if (Directory.Exists(target))
            {
                var answer = MessageBox.Show(this,
                    $"已经装了 {inspection.Manifest.DisplayName}。\n" +
                    $"换成包里的 {inspection.Manifest.Version}？\n" +
                    "旧的会留在 modules\\.backup，装坏了能拿回来。",
                    "覆盖", MessageBoxButton.OKCancel, MessageBoxImage.Question);
                if (answer != MessageBoxResult.OK)
                {
                    SetStatus("没换");
                    return;
                }
                overwrite = true;
            }

            SetStatus($"在装 {inspection.Manifest.DisplayName}…");

            var result = _installer.Install(path, overwrite);
            if (!result.Success)
            {
                SetStatus(result.Message, true);
                return;
            }

            _report = _host.ReloadAll();
            RefreshModules();
            ShowHome();

            var loaded = _report.Entries.FirstOrDefault(m => string.Equals(m.Id, result.ModuleId, StringComparison.OrdinalIgnoreCase));
            if (loaded != null && loaded.State == ModuleState.Started)
                SetStatus($"{result.Message}。能用了。");
            else if (loaded != null)
                SetStatus($"{result.Message}，但没起来：{loaded.Error}", true);
            else
                SetStatus(result.Message);
        }

        private void BtnUninstall_Click(object sender, RoutedEventArgs e)
        {
            if (!(sender is FrameworkElement element) || !(element.Tag is string moduleId)) return;

            var answer = MessageBox.Show(this,
                $"卸掉 {moduleId}？\n\n" +
                "文件会被移出模块目录，重启后彻底不见。",
                "卸载", MessageBoxButton.OKCancel, MessageBoxImage.Warning);
            if (answer != MessageBoxResult.OK) return;

            if (!_installer.Uninstall(moduleId, out var message))
            {
                SetStatus(message, true);
                return;
            }

            _report = _host.ReloadAll();
            RefreshModules();
            SetStatus($"{moduleId} 卸了。{message}");
        }

        // ── 窗口 ────────────────────────────────────────────────────────────

        private void TitleBar_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            if (e.ClickCount == 2)
            {
                WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;
                return;
            }
            if (e.ButtonState == MouseButtonState.Pressed) DragMove();
        }

        private void BtnMax_Click(object sender, RoutedEventArgs e) =>
            WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;

        /// <summary>
        /// WindowChrome 的经典毛病：最大化时窗口会比屏幕大出"缩放边框"那一圈，
        /// 内容跟着外扩，右上角的关闭按钮就被顶出屏幕 —— 看着像"位置错位"。
        /// 这里把根容器按边框厚度缩回来，正常状态下归零。
        /// </summary>
        protected override void OnStateChanged(EventArgs e)
        {
            base.OnStateChanged(e);
            if (RootGrid == null) return;

            if (WindowState == WindowState.Maximized)
            {
                var border = SystemParameters.WindowResizeBorderThickness;
                RootGrid.Margin = new Thickness(border.Left + 1, border.Top + 1, border.Right + 1, border.Bottom + 1);
            }
            else
            {
                RootGrid.Margin = new Thickness(0);
            }
        }

        private void BtnMin_Click(object sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;

        private void BtnClose_Click(object sender, RoutedEventArgs e) => Close();

        private void BtnOpenModule_Click(object sender, RoutedEventArgs e)
        {
            if (!(sender is FrameworkElement element) || !(element.Tag is string moduleId)) return;
            OpenModuleById(moduleId);
        }

        public sealed class FrameworkCard
        {
            public string Id { get; set; }
            public string Name { get; set; }
            public string Version { get; set; }
            public string State { get; set; }
            public string Detail { get; set; }
            public string Initial { get; set; }
            public bool CanUninstall { get; set; }
            public string Kind { get; set; }
            public bool CanOpen { get; set; } = true;
            public bool IsBase { get; set; }
            public string BaseLabel { get; set; } = "";
            public string Category { get; set; } = "framework";
            public string CategoryLabel { get; set; } = "框架";
            public string Icon { get; set; }
            public string Accent { get; set; }
            public int Order { get; set; } = 100;
            public bool Desktop { get; set; } = true;
            public Visibility BaseVisibility => IsBase ? Visibility.Visible : Visibility.Collapsed;

            /// <summary>桌面图标上的一个字：清单里写了就用，否则取名字首字。</summary>
            public string IconText => string.IsNullOrWhiteSpace(Icon)
                ? (string.IsNullOrEmpty(Name) ? "P" : Name.Substring(0, 1))
                : Icon.Trim();

            /// <summary>
            /// 图标底色：清单给了 accent 就用，没给就按 id 哈希生成。
            /// 同一个模块每次启动颜色都一样，这是「看起来像真的桌面」的关键。
            /// </summary>
            public System.Windows.Media.Brush AccentBrush
            {
                get
                {
                    var color = ParseColor(Accent);
                    if (color == null)
                    {
                        var palette = Trm.ArtColors;
                        var hash = 0;
                        foreach (var c in Id ?? Name ?? "") hash = (hash * 31 + c) & 0x7FFFFFFF;
                        color = palette[hash % palette.Length];
                    }
                    var brush = new System.Windows.Media.LinearGradientBrush(
                        Lighten(color.Value, 0.12), color.Value,
                        new System.Windows.Point(0, 0), new System.Windows.Point(1, 1));
                    brush.Freeze();
                    return brush;
                }
            }

            internal static System.Windows.Media.Color? ParseColor(string text)
            {
                if (string.IsNullOrWhiteSpace(text)) return null;
                try { return (System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString(text.Trim()); }
                catch { return null; }
            }

            private static System.Windows.Media.Color Lighten(System.Windows.Media.Color c, double amount)
                => System.Windows.Media.Color.FromRgb(
                    (byte)(c.R + (255 - c.R) * amount),
                    (byte)(c.G + (255 - c.G) * amount),
                    (byte)(c.B + (255 - c.B) * amount));

            /// <summary>状态胶囊的底色/字色：跑着的是绿，没跑的是灰，出错的是红。</summary>
            public System.Windows.Media.Brush StatePill => StateBrush(true);
            public System.Windows.Media.Brush StateInk => StateBrush(false);

            private System.Windows.Media.Brush StateBrush(bool background)
            {
                var state = State ?? "";
                if (state.Contains("失败") || state.Contains("错"))
                    return background ? Trm.DangerSoft : Trm.DangerInk;
                if (state.Contains("运行中") || state.Contains("已装载") || state.Contains("安装"))
                    return background ? Trm.OkSoft : Trm.OkInk;
                return background ? Trm.Chip : Trm.ChipInk;
            }

        }

        /// <summary>市场卡片的可绑定包装：数据来自清单，状态来自本机已装模块。</summary>
        public sealed class MarketCard : System.ComponentModel.INotifyPropertyChanged
        {
            private string _action = "安装";
            private bool _canInstall = true;

            public MarketCard(MarketItem item) { Item = item; }

            public MarketItem Item { get; }
            public string Id => Item.Id;
            public string Name => Item.Name;
            public string Version => Item.Version;
            public string Summary => Item.Summary;
            public string Category => Item.Category;
            public string IconText => Item.IconText;
            public bool Offline => Item.Offline;
            public Visibility OfficialVisibility => Item.Official ? Visibility.Visible : Visibility.Collapsed;
            public string SourceLine => (Item.Source ?? "") + (string.IsNullOrEmpty(Item.SizeText) ? "" : "  ·  " + Item.SizeText);

            public System.Windows.Media.Brush AccentBrush
            {
                get
                {
                    var color = FrameworkCard.ParseColor(Item.Accent)
                                ?? Trm.ColorOf("TrmAccent");
                    var brush = new System.Windows.Media.SolidColorBrush(color);
                    brush.Freeze();
                    return brush;
                }
            }

            public string ActionText
            {
                get => _action;
                set { _action = value; Changed("ActionText"); }
            }

            public bool CanInstall
            {
                get => _canInstall;
                set { _canInstall = value; Changed("CanInstall"); }
            }

            /// <summary>已装版本与清单版本比较，决定按钮文案。</summary>
            public void ApplyInstalled(string current)
            {
                if (string.IsNullOrEmpty(current)) { ActionText = "安装"; CanInstall = true; return; }
                if (IsNewer(Item.Version, current)) { ActionText = "更新"; CanInstall = true; return; }
                ActionText = "已装";
                CanInstall = false;
            }

            private static bool IsNewer(string candidate, string current)
            {
                if (!System.Version.TryParse((candidate ?? "").Split('-')[0], out var a)) return false;
                if (!System.Version.TryParse((current ?? "").Split('-')[0], out var b)) return true;
                return a > b;
            }

            public event System.ComponentModel.PropertyChangedEventHandler PropertyChanged;
            private void Changed(string name) => PropertyChanged?.Invoke(this,
                new System.ComponentModel.PropertyChangedEventArgs(name));
        }

        // ── 服务设置 ────────────────────────────────────────────────────
        // 凭证由服务端每 10 分钟轮换，本地不生成、不落盘；解锁只认「当前这一把」，
        // 凭证一换（或断网）就自动退出，避免输过一次就一直是维护状态。
        // 市场的开关是本地设置：关掉后这台机器一次线上请求都不发。

        private void OnUi(Action action)
        {
            if (Dispatcher.CheckAccess()) { action(); return; }
            try { Dispatcher.Invoke(action); } catch { /* 窗口已关，晚到的事件丢掉 */ }
        }

        private void SyncMarketToggle()
            => BtnAdminToggleMarket.Content = _admin.MarketEnabled ? "关闭市场" : "开启市场";

        private void RefreshAdminKeyView()
        {
            var info = _adminKey?.Current;
            if (info == null)
            {
                LabAdminKey.Text = "—— —— ——";
                LabAdminKeyExpire.Text = "凭据模块没加载";
                LabAdminKeyError.Text = "";
                return;
            }
            LabAdminKey.Text = info.Online ? info.Key : "—— —— ——";
            LabAdminKeyExpire.Text = info.Online
                ? info.ExpiresText + " 后更换 · 服务器时间 " + info.ServerTime
                : "等服务器回来再试";
            LabAdminKeyError.Text = info.Online ? "" : "取不到凭证：" + info.Error;
        }

        private void OnAdminKeyChanged()
        {
            RefreshAdminKeyView();
            if (!_adminUnlocked) return;
            var info = _adminKey?.Current;
            if (info == null || !info.Online || !string.Equals(info.Key, _unlockKey, StringComparison.Ordinal))
            {
                LockAdmin(info != null && info.Online
                    ? "服务器换了凭据，维护模式已自动退出，请用新凭据重新解锁"
                    : "服务器断了，维护模式已自动退出");
                return;
            }
            LabAdminRemain.Text = "当前凭证还有 " + info.ExpiresText + " 有效";
        }

        private void LockAdmin(string reason)
        {
            _adminUnlocked = false;
            _unlockKey = "";
            PanAdminLocked.Visibility = Visibility.Visible;
            PanAdminUnlocked.Visibility = Visibility.Collapsed;
            PanAdminTools.IsEnabled = false;
            LabAdminAction.Text = reason;
            SetStatus(reason, true);
        }

        private void RefreshEndpoints()
        {
            _endpoints.Clear();
            if (_gateway == null)
            {
                LabAdminApiInfo.Text = "API 接口模块没加载，接口表暂时是空的";
                return;
            }
            LabAdminApiInfo.Text = "服务端 " + _gateway.Site + " · " + _gateway.Endpoints.Count
                + " 个接口 · 接入密钥" + (string.IsNullOrEmpty(_gateway.AccessKey) ? "未设置" : "已设置");
            foreach (var ep in _gateway.Endpoints) _endpoints.Add(new EndpointCard(ep));
        }

        // ── 外观（TRM 浅色 / 深色两套令牌）────────────────────────────────

        private void RefreshThemeButtons()
        {
            var dark = TrmThemeService.IsDark;
            BtnThemeLight.IsEnabled = dark;
            BtnThemeDark.IsEnabled = !dark;
            // 标题栏那个字表示「点一下会切到哪一套」，不是当前是哪一套
            LabThemeToggle.Text = dark ? "浅" : "深";
            BtnThemeToggle.ToolTip = dark ? "切到浅色外观" : "切到深色外观";
            LabThemeState.Text = (dark ? "深色" : "浅色")
                + " · 颜色只从设计令牌取，换主题不用重开窗口";
        }

        private void BtnThemeToggle_Click(object sender, RoutedEventArgs e)
        {
            TrmThemeService.Apply(!TrmThemeService.IsDark, _settings);
            RefreshThemeButtons();
            SetStatus(TrmThemeService.IsDark ? "已切到深色" : "已切到浅色");
        }

        // ── 维护入口：不在导航里，也没有任何提示 ──────────────────────────────
        // 版本号 1 秒内连点 7 下才进得去。普通用户不会去连点一个版本号，
        // 界面上也不会出现任何维护入口的字样。
        //
        // 为什么是「1 秒 7 下」而不是「1.5 秒 5 下」：时间窗收紧、次数抬高之后，
        // 手滑连点两下、或者无聊戳几下，都远远够不着；而真要进去的人
        // 一秒点七下并不难。这个组合是靠「门槛高度」藏，不是靠「猜不到」藏。
        private const int UnlockTaps = 7;
        private const double UnlockWindowSeconds = 1.0;

        private int _versionTaps;
        private DateTime _lastVersionTap = DateTime.MinValue;

        private void VersionBadge_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            e.Handled = true;   // 别把这一下当成拖标题栏
            var now = DateTime.Now;
            _versionTaps = (now - _lastVersionTap).TotalSeconds <= UnlockWindowSeconds ? _versionTaps + 1 : 1;
            _lastVersionTap = now;
            if (_versionTaps < UnlockTaps) return;
            _versionTaps = 0;
            ShowView("api");
        }

        /// <summary>离屏渲染 / 自检用：直接进 API 页，不用模拟连点。</summary>
        public void ShowApiViewForRender() => ShowView("api");

        /// <summary>离屏渲染用：直接切到某一段。</summary>
        public void ShowApiSegmentForRender(string segment)
        {
            ShowView("api");
            ShowApiSegment(string.IsNullOrWhiteSpace(segment) ? "overview" : segment);
        }

        private void BtnThemeLight_Click(object sender, RoutedEventArgs e)
        {
            TrmThemeService.Apply(false, _settings);
            RefreshThemeButtons();
            SetStatus("已切到浅色");
        }

        private void BtnThemeDark_Click(object sender, RoutedEventArgs e)
        {
            TrmThemeService.Apply(true, _settings);
            RefreshThemeButtons();
            SetStatus("已切到深色");
        }

        // ── 解锁 / 退出 / 凭证 ──────────────────────────────────────────────

        private void BtnAdminUnlock_Click(object sender, RoutedEventArgs e)
        {
            if (_adminKey == null) { LabAdminKeyError.Text = "凭据模块没加载"; return; }
            var info = _adminKey.Current;
            if (info == null || !info.Online)
            {
                LabAdminKeyError.Text = "服务器还没给凭证，断网时不能新解锁";
                return;
            }
            if (!_adminKey.Validate(TxtAdminKeyInput.Text))
            {
                LabAdminKeyError.Text = "凭证不对，请输入服务设置页当前显示的那一串";
                return;
            }
            _adminUnlocked = true;
            _unlockKey = (info.Key ?? "").Trim();
            PanAdminLocked.Visibility = Visibility.Collapsed;
            PanAdminUnlocked.Visibility = Visibility.Visible;
            PanAdminTools.IsEnabled = true;
            LabAdminRemain.Text = "当前凭证还有 " + info.ExpiresText + " 有效";
            TxtAdminKeyInput.Clear();
            LabAdminKeyError.Text = "";
            LabAdminAction.Text = "已解锁，可以管市场、接口和检查更新了";
            SetStatus("维护模式已开启");
        }

        private async void BtnAdminRefreshKey_Click(object sender, RoutedEventArgs e)
        {
            if (_adminKey == null) return;
            LabAdminKeyError.Text = "正在连服务器…";
            var info = await _adminKey.RefreshAsync();
            LabAdminKeyError.Text = info.Online ? "" : "刷新失败：" + info.Error;
            SetStatus(info.Online ? "凭据已刷新" : "凭证没刷新成功", !info.Online);
        }

        private void BtnAdminSaveAccessKey_Click(object sender, RoutedEventArgs e)
        {
            var key = TxtAdminAccessKey.Text.Trim();
            _gateway?.SetAccessKey(key);
            _adminKey?.SetAccessKey(key);
            RefreshEndpoints();
            SetStatus("接入密钥已保存，正在用它重连");
        }

        private void BtnAdminExit_Click(object sender, RoutedEventArgs e) => LockAdmin("已退出维护模式");

        // ── 市场状态与开关 ──────────────────────────────────────────────────

        private void BtnAdminToggleMarket_Click(object sender, RoutedEventArgs e)
        {
            var next = !_admin.MarketEnabled;
            _admin.SetMarketEnabled(next);
            SyncMarketToggle();
            if (!next)
            {
                _market.Clear();
                LabNoMarket.Visibility = Visibility.Visible;
            }
            LabAdminAction.Text = next
                ? "市场已开启，进市场页会重新拉线上清单"
                : "市场已关闭，这台机器不再拉线上清单（服务端没被改）";
            SetStatus(next ? "市场已开启" : "市场已关闭");
        }

        private async void BtnAdminCheck_Click(object sender, RoutedEventArgs e)
        {
            if (_adminBusy) return;
            _adminBusy = true;
            try
            {
                LabAdminAction.Text = "正在读服务器状态…";
                var s = await _admin.CheckStatusAsync();
                LabAdminMarketState.Text = "本机市场：" + (s.Enabled ? "开启" : "已关闭")
                    + " · 上次来源 " + s.Origin + " · " + s.Items + " 个条目"
                    + (string.IsNullOrEmpty(s.Message) ? "" : " · " + s.Message);
                LabAdminServerState.Text = s.ServerAvailable
                    ? "服务器：在线 · 市场" + (s.ServerMarketEnabled ? "开启" : "关闭")
                      + " · " + s.ServerItems + " 个条目 · 更新于 " + s.Updated
                    : "服务器：读不到（离线或响应被截断），本机功能不受影响";
                LabAdminNotice.Text = s.Notice ?? "";
                LabAdminAction.Text = s.ServerAvailable ? "本机 + 服务端状态都已更新" : "只拿到本机状态";
                SyncMarketToggle();
            }
            catch (Exception ex) { LabAdminAction.Text = "检查失败：" + ex.Message; }
            finally { _adminBusy = false; }
        }

        private async void BtnAdminRefreshMarket_Click(object sender, RoutedEventArgs e)
        {
            if (_adminBusy) return;
            _adminBusy = true;
            try
            {
                LabAdminAction.Text = "正在强制拉线上清单…";
                var result = await _admin.ForceRefreshAsync();
                ApplyMarketResult(result);
                LabAdminAction.Text = result.Success
                    ? "清单已刷新：" + result.Origin + " · " + (result.Catalog?.Items.Count ?? 0) + " 个条目"
                    : "清单没拉下来：" + result.Message;
            }
            catch (Exception ex) { LabAdminAction.Text = "刷新失败：" + ex.Message; }
            finally { _adminBusy = false; }
        }

        private void BtnAdminClearCache_Click(object sender, RoutedEventArgs e)
        {
            var ok = _admin.ClearCache();
            LabAdminAction.Text = ok
                ? "本地清单缓存已清掉，下次进市场会重新拉"
                : "缓存清不掉（可能被别的程序占用）";
            SetStatus(ok ? "市场缓存已清" : "缓存清不掉", !ok);
        }

        // ── 自动更新 ────────────────────────────────────────────────────────

        /// <summary>两个通道都问，取更新的那个；只提示、只给链接，不静默下载。</summary>
        private async Task CheckUpdateAsync(bool verbose)
        {
            try
            {
                if (verbose) LabAdminUpdate.Text = "正在问博客服务端和 GitHub…";
                var info = await _updates.CheckAsync();
                LabAdminUpdate.Text = "当前 " + info.CurrentVersion
                    + " · 服务端 " + info.ServerVersion
                    + " · GitHub " + info.GitHubVersion + "\n" + info.Message;
                PanUpdateTip.Visibility = info.HasUpdate ? Visibility.Visible : Visibility.Collapsed;
                if (info.HasUpdate)
                    LabUpdateTip.Text = "发现新版本 " + info.LatestVersion + "（当前 "
                        + info.CurrentVersion + "），点这里去 GitHub 下载安装包。";
                if (verbose) SetStatus(info.HasUpdate ? "有新版本 " + info.LatestVersion : "已是最新版");
            }
            catch (Exception ex)
            {
                if (verbose)
                {
                    LabAdminUpdate.Text = "检查失败：" + ex.Message;
                    SetStatus("更新检查失败", true);
                }
            }
        }

        private async void BtnAdminCheckUpdate_Click(object sender, RoutedEventArgs e)
            => await CheckUpdateAsync(true);

        private void UpdateTip_Click(object sender, MouseButtonEventArgs e) => OpenRelease();

        private void BtnAdminGoGitHub_Click(object sender, RoutedEventArgs e) => OpenRelease();

        private void OpenRelease()
        {
            try { _updates.OpenRelease(); }
            catch (Exception ex) { SetStatus(ex.Message, true); }
        }

        // ── API 接口管理 ────────────────────────────────────────────────────

        private static string EndpointId(object sender)
            => (sender as FrameworkElement)?.Tag as string;

        private void BtnAdminToggleEndpoint_Click(object sender, RoutedEventArgs e)
        {
            if (_gateway == null) return;
            var id = EndpointId(sender);
            var ep = _gateway.Endpoints.FirstOrDefault(x => string.Equals(x.Id, id, StringComparison.OrdinalIgnoreCase));
            if (ep == null) return;
            var was = ep.Enabled;
            _gateway.SetEnabled(id, !was);
            RefreshEndpoints();
            LabAdminApiResult.Text = (was ? "已停用：" : "已启用：") + ep.Name;
        }

        private async void BtnAdminTestEndpoint_Click(object sender, RoutedEventArgs e)
        {
            if (_gateway == null) return;
            var id = EndpointId(sender);
            if (string.IsNullOrEmpty(id)) return;
            LabAdminApiResult.Text = "正在测 " + id + " …";
            var result = await _gateway.CallAsync(id, "pcv-probe");
            var body = (result.Body ?? "").Replace("\r", " ").Replace("\n", " ");
            if (body.Length > 160) body = body.Substring(0, 160) + "…";
            LabAdminApiResult.Text = (result.Ok ? "通了" : "没通") + " · HTTP " + result.Status
                + " · " + result.Message + (string.IsNullOrEmpty(body) ? "" : " · " + body);
        }

        /// <summary>接口表的可绑定包装；接口清单只有一个真相源（服务端的 interfaces.json）。</summary>
        public sealed class EndpointCard
        {
            private readonly ApiEndpointInfo _ep;
            public EndpointCard(ApiEndpointInfo ep) { _ep = ep; }

            public string Id => _ep.Id;
            public string Name => _ep.Name;
            public string Note => _ep.Note;
            public string MethodText => _ep.Method + " " + _ep.Path + (_ep.NeedsKey ? "  ·  要密钥" : "");
            public string StateText => _ep.Enabled ? "已启用" : "已停用";
            public string ToggleText => _ep.Enabled ? "停用" : "启用";
            public Brush StateBrush
            {
                get
                {
                    return _ep.Enabled ? Trm.OkInk : Trm.Meta;
                }
            }
        }
    }
}
