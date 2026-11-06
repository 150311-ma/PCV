using System;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using Pcv.Modular.Abstractions;
using Pcv.Modular.Host;
using Pcv.Modular.Packages;

namespace Pcv.Shell
{
    /// <summary>
    /// 两页：首页（装包 + 已装列表）和"加一个包"（导入 / 自己做）。
    /// 没有第三页，也没有设置页——PCV 本身没有功能可设置。
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

            var loaded = _report.Entries.ToDictionary(e => e.Id, e => e, StringComparer.OrdinalIgnoreCase);

            foreach (var installed in _installer.ListInstalled())
            {
                loaded.TryGetValue(installed.Id, out var entry);
                var manifest = ModuleHostAccessor.ReadManifest(installed.Directory);
                var isProcess = string.Equals(manifest?.Kind, "process", StringComparison.OrdinalIgnoreCase);
                var isSoftware = manifest != null && manifest.IsSoftware;
                var isBase = manifest != null && manifest.IsBaseFramework;

                string state;
                if (entry != null) state = StateText(entry.State);
                else if (isProcess) state = _accessor.IsRunning(installed.Id) ? "运行中" : "未运行";
                else state = "未加载";

                var detail = entry?.Error ?? manifest?.Description ?? (isProcess ? "独立进程框架" : "自带");

                // "必须要有界面"：程序集型框架没实现 IFrameworkSurface 就是没有界面，
                // 这里如实标出来，而不是给个点不开的按钮
                var hasSurface = entry?.Instance is Pcv.Modular.Contracts.IFrameworkSurface;
                var noSurface = entry != null && !isProcess && !hasSurface && isBase == false && isSoftware == false;

                _cardsTarget(isSoftware).Add(new FrameworkCard
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
                    Category = isSoftware ? "software" : "framework"
                });
            }

            LabFrameworkTitle.Text = $"共 {_frameworks.Count} 个 · 提供能力，被软件调用";
            LabSoftwareCount.Text = _software.Count + " 个";
            LabNoFramework.Visibility = _frameworks.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
            LabNoSoftware.Visibility = _software.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
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

        private void BtnPasteScript_Click(object sender, RoutedEventArgs e)
        {
            new ScriptWindow(_accessor) { Owner = this }.ShowDialog();
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

            DropFrame.Stroke = accepted
                ? (Brush)FindResource("ColorBrush3")
                : new SolidColorBrush(Color.FromRgb(0x8F, 0xA9, 0xC6));
            DropFrame.StrokeThickness = accepted ? 2.2 : 1.6;
            DropFrame.Fill = new SolidColorBrush(Color.FromArgb(accepted ? (byte)0x66 : (byte)0x33, 0xFF, 0xFF, 0xFF));
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

            if (IsScriptPath(dropped)) RunBuildScript(dropped);
            else InstallPackage(dropped);
        }

        private void ResetDropZone()
        {
            DropFrame.Stroke = new SolidColorBrush(Color.FromRgb(0x8F, 0xA9, 0xC6));
            DropFrame.StrokeThickness = 1.6;
            DropFrame.Fill = new SolidColorBrush(Color.FromArgb(0x33, 0xFF, 0xFF, 0xFF));
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
            public Visibility BaseVisibility => IsBase ? Visibility.Visible : Visibility.Collapsed;
        }
    }
}
