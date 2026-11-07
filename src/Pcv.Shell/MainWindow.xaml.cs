using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using Pcv.Modular.Abstractions;
using Pcv.Modular.Host;
using Pcv.Modular.Packages;

namespace Pcv.Shell
{
    /// <summary>
    /// 四页：软件 / 安装 / 框架 / 版权，左侧导航切换。
    /// 自检在独立窗口里（侧栏底部的「运行一次自检」），不占导航项——
    /// 它不是一页内容，是一次性的检查结果。
    /// </summary>
    public partial class MainWindow : Window
    {
        private const string DeepSeekUrl = "https://chat.deepseek.com/";

        private readonly ModuleHost _host;
        private readonly ModuleHostAccessor _accessor;
        private readonly PackageInstaller _installer;
        private readonly ObservableCollection<FrameworkCard> _frameworks = new ObservableCollection<FrameworkCard>();
        private readonly ObservableCollection<FrameworkCard> _software = new ObservableCollection<FrameworkCard>();
        private ModuleLoadReport _report;

        public MainWindow(ModuleHost host, ModuleLoadReport report)
        {
            _host = host;
            _report = report;
            InitializeComponent();

            _installer = new PackageInstaller(host.ModulesDirectory, host.Log);
            _accessor = new ModuleHostAccessor(host);
            ListFrameworks.ItemsSource = _frameworks;
            ListSoftware.ItemsSource = _software;
            LabVersion.Text = "PCV " + host.Host.Version;
            RefreshModules();
            ShowView("home");

            Loaded += (s, e) => GlassHelper.TryEnableRoundedCorners(this);
        }

        // ── 列表 ────────────────────────────────────────────────────────────

        private void RefreshModules()
        {
            _frameworks.Clear();
            _software.Clear();
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
                    CategoryLabel = label
                };

                if (isSoftware)
                {
                    _software.Add(card);
                }
                else if (!manifest.IsBuiltInCategory)
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

            foreach (var pair in extras.OrderBy(p => p.Key, StringComparer.OrdinalIgnoreCase))
                PanExtraCategories.Children.Add(BuildCategoryGroup(pair.Key, pair.Value));

            var total = _frameworks.Count + extras.Values.Sum(list => list.Count);
            LabFrameworkTitle.Text = $"共 {total} 个 · 提供能力，被软件调用";
            LabSoftwareCount.Text = _software.Count + " 个";
            LabNoSoftware.Visibility = _software.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
            LabNoFramework.Visibility = total == 0 ? Visibility.Visible : Visibility.Collapsed;
        }

        /// <summary>给自定义分类生成一组：小标题 + 卡片列表。</summary>
        private FrameworkElement BuildCategoryGroup(string label, ObservableCollection<FrameworkCard> cards)
        {
            var template = (DataTemplate)FindResource("FrameworkCardTemplate");

            var panel = new StackPanel { Margin = new Thickness(0, 20, 0, 0) };
            panel.Children.Add(new TextBlock
            {
                Text = label + "（" + cards.Count + " 个）",
                FontSize = 11.5,
                FontWeight = FontWeights.SemiBold,
                Foreground = new SolidColorBrush(Color.FromRgb(0x9A, 0xA4, 0xB2)),
                Margin = new Thickness(2, 0, 0, 8)
            });
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

        private void SetStatus(string message, bool isError = false)
        {
            LabStatus.Text = message;
            LabStatus.Foreground = isError
                ? new SolidColorBrush(Color.FromRgb(0xC0, 0x39, 0x2B))
                : new SolidColorBrush(Color.FromRgb(0x4A, 0x55, 0x63));
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
            ViewCredits.Visibility = view == "credits" ? Visibility.Visible : Visibility.Collapsed;

            Highlight(NavSoftware, view == "home");
            Highlight(NavInstall, view == "install");
            Highlight(NavFrameworks, view == "frameworks");
            Highlight(NavCredits, view == "credits");

            if (view == "frameworks") RefreshModules();
        }

        private static void Highlight(System.Windows.Controls.Button button, bool selected)
        {
            if (button == null) return;
            button.Background = new System.Windows.Media.SolidColorBrush(
                selected ? System.Windows.Media.Color.FromArgb(0x26, 0x13, 0x70, 0xF3)
                         : System.Windows.Media.Colors.Transparent);
            button.Foreground = new System.Windows.Media.SolidColorBrush(
                selected ? System.Windows.Media.Color.FromRgb(0x13, 0x70, 0xF3)
                         : System.Windows.Media.Color.FromRgb(0x5B, 0x64, 0x72));
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
            DropZone.BorderBrush = new SolidColorBrush(active
                ? Color.FromRgb(0x13, 0x70, 0xF3)
                : Color.FromRgb(0xD7, 0xE3, 0xF4));
            DropZone.BorderThickness = new Thickness(active ? 2 : 1.5);
            DropZone.Background = new SolidColorBrush(active
                ? Color.FromRgb(0xEF, 0xF5, 0xFF)
                : Colors.White);
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
            public Visibility BaseVisibility => IsBase ? Visibility.Visible : Visibility.Collapsed;

            /// <summary>状态胶囊的底色/字色：跑着的是绿，没跑的是灰，出错的是红。</summary>
            public System.Windows.Media.Brush StatePill => StateBrush(true);
            public System.Windows.Media.Brush StateInk => StateBrush(false);

            private System.Windows.Media.Brush StateBrush(bool background)
            {
                var state = State ?? "";
                if (state.Contains("失败") || state.Contains("错"))
                    return Solid(background, 0xFD, 0xEC, 0xEC, 0xB4, 0x23, 0x2E);
                if (state.Contains("运行中") || state.Contains("已装载") || state.Contains("安装"))
                    return Solid(background, 0xEA, 0xF7, 0xEE, 0x1B, 0x8A, 0x4B);
                return Solid(background, 0xF1, 0xF4, 0xF8, 0x6B, 0x76, 0x88);
            }

            private static System.Windows.Media.Brush Solid(bool background, byte br, byte bg, byte bb,
                byte fr, byte fg, byte fb)
            {
                var brush = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(
                    background ? br : fr, background ? bg : fg, background ? bb : fb));
                brush.Freeze();
                return brush;
            }
        }
    }
}
