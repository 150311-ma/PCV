using System;
using System.Collections.Generic;
using System.IO;
using System.Windows;
using Pcv.Modular.Host;

namespace Pcv.Shell
{
    /// <summary>
    /// 壳程序：不写任何业务代码，只做三件事——
    /// 把加载器跑起来、把状态显示出来、退出时按逆序关掉模块。
    /// </summary>
    public partial class App : Application
    {
        public App()
        {
            // 必须赶在任何窗口之前
            DpiHelper.Enable();
        }

        private ModuleHost _host;
        private System.Windows.Forms.NotifyIcon _tray;
        private MainWindow _window;

        protected override void OnStartup(StartupEventArgs e)
        {
            base.OnStartup(e);

            // 崩了要留下证据，否则用户只看到"窗口一闪就没了"
            DispatcherUnhandledException += (s, args) =>
            {
                LogCrash(args.Exception);
                args.Handled = true;
                MessageBox.Show("出了个意外：\n" + args.Exception.Message + "\n\n细节写进了 logs\\crash.log",
                    "PAINCHARNWAV", MessageBoxButton.OK, MessageBoxImage.Warning);
            };
            AppDomain.CurrentDomain.UnhandledException += (s, args) => LogCrash(args.ExceptionObject as Exception);

            var baseDirectory = AppDomain.CurrentDomain.BaseDirectory;

            // 安全自检必须赶在任何模块被加载之前：
            // 一旦第三方程序集进了进程，再判断「有没有被塞东西」就晚了。
            SecurityGuard.Initialize();

            // 命令行跑构建脚本：界面之外也能验"拖进来会怎样"
            var buildIndex = Array.IndexOf(e.Args, "--build");
            if (buildIndex >= 0 && buildIndex + 1 < e.Args.Length)
            {
                var buildLog = Path.Combine(baseDirectory, "logs", "build.log");
                var logger = new Pcv.Modular.Host.ModuleLogger("build", buildLog, false);
                var builder = new ScriptBuilder(baseDirectory, logger);
                var lines = new System.Collections.Generic.List<string>();
                // 这里 _host 还没建出来（这一步在宿主初始化之前），只能用最土的方式记日志
                var result = builder.Run(e.Args[buildIndex + 1], line => lines.Add(line));
                lines.Add("");
                lines.Add("── 结果 ──");
                lines.Add(result.Message);
                lines.Add("退出码=" + result.ExitCode + " 产物=" + (result.PackagePath ?? "无"));

                // WinExe 没有控制台，结果只能落文件
                try
                {
                    var folder = Path.Combine(baseDirectory, "logs");
                    Directory.CreateDirectory(folder);
                    File.WriteAllText(Path.Combine(folder, "last-build.log"), string.Join(Environment.NewLine, lines));
                }
                catch
                {
                    // 写不了就算了
                }
                // 顺手装上：拖进界面也是这条路，行为要一致（--no-install 可跳过）
                if (result.Success && Array.IndexOf(e.Args, "--no-install") < 0)
                {
                    try
                    {
                        var installer = new Pcv.Modular.Packages.PackageInstaller(
                            Path.Combine(baseDirectory, "modules"), logger);
                        var install = installer.Install(result.PackagePath, true);
                        lines.Add("安装：" + install.Message);
                        File.WriteAllText(Path.Combine(baseDirectory, "logs", "last-build.log"),
                            string.Join(Environment.NewLine, lines));
                        Shutdown(install.Success ? 0 : 3);
                        return;
                    }
                    catch (Exception ex)
                    {
                        lines.Add("安装出错：" + ex.Message);
                        File.WriteAllText(Path.Combine(baseDirectory, "logs", "last-build.log"),
                            string.Join(Environment.NewLine, lines));
                        Shutdown(4);
                        return;
                    }
                }

                Shutdown(result.Success ? 0 : 2);
                return;
            }

            // 命令行自检：提示词能不能取到、能不能进剪贴板（界面之外也能验）
            if (Array.IndexOf(e.Args, "--copy-prompt") >= 0)
            {
                var prompt = PromptLibrary.Load();
                var ok = ClipboardWriter.TrySetText(prompt, 6);
                Console.WriteLine($"promptChars={prompt.Length} clipboard={ok}");
                Shutdown(ok && prompt.Length > 500 ? 0 : 2);
                return;
            }
            // 创作者工具的离屏渲染：确认这个窗口真的画出来了。--creator <包路径>
            var creatorIndex = Array.IndexOf(e.Args, "--creator");
            if (creatorIndex >= 0)
            {
                var creatorWindow = new CreatorWindow();
                MainWindow = creatorWindow;
                var target = creatorIndex + 2 < e.Args.Length
                    ? e.Args[creatorIndex + 2]
                    : Path.Combine(baseDirectory, "creator.png");
                if (creatorIndex + 1 < e.Args.Length)
                    creatorWindow.LoadPackageForRender(e.Args[creatorIndex + 1]);
                try
                {
                    RenderToFile(creatorWindow, target, 820, 700);
                    Console.WriteLine("已渲染：" + target);
                    Shutdown(0);
                }
                catch (Exception ex)
                {
                    Console.WriteLine("渲染失败：" + ex);
                    Shutdown(2);
                }
                return;
            }

            // 安全告警窗口的离屏渲染：用来确认它真的画出来了、内容没被裁掉。
            // 只在显式指定时走，正常启动不会出现这个窗口。
            var alertIndex = Array.IndexOf(e.Args, "--render-alert");
            if (alertIndex >= 0)
            {
                var target = alertIndex + 1 < e.Args.Length
                    ? e.Args[alertIndex + 1]
                    : Path.Combine(baseDirectory, "alert.png");
                var sample = new List<Threat>
                {
                    new Threat(ThreatLevel.Fatal, "PCV 的文件被改过",
                        "下面这些文件的指纹和发布时对不上：\n"
                        + "modules/pcv.core/Pcv.Core.dll（内容变了）\n"
                        + "请从官方渠道重新下载 PCV。"),
                    new Threat(ThreatLevel.Fatal, "检测到远程调试器",
                        "另一个进程正连着 PCV 做调试。请关掉那个程序再启动。"),
                };
                var alertWindow = new SecurityAlertWindow(sample);
                try
                {
                    RenderToFile(alertWindow, target, 600, 600);
                    Console.WriteLine("已渲染：" + target);
                    Shutdown(0);
                }
                catch (Exception ex)
                {
                    Console.WriteLine("渲染失败：" + ex);
                    Shutdown(2);
                }
                return;
            }

            var modulesDirectory = Path.Combine(baseDirectory, "modules");

            // 上次因为"模块正在跑、文件被占用"而排队的安装，在这里换上。
            // 必须在 ModuleHost 扫目录之前——那时还没有任何 dll 被加载，目录随便挪。
            try
            {
                var applied = Pcv.Modular.Packages.PackageInstaller.ApplyPending(modulesDirectory,
                    message => Console.WriteLine(message));
                if (applied.Count > 0)
                    Console.WriteLine("已应用排队安装：" + string.Join("、", applied));
            }
            catch (Exception ex)
            {
                Console.WriteLine("应用排队安装失败：" + ex.Message);
            }

            _host = new ModuleHost(new ModuleHostOptions
            {
                ModulesDirectory = modulesDirectory,
                LogFile = Path.Combine(baseDirectory, "logs", "modules.log"),
                EchoToConsole = false,
                HostName = "PAINCHARNWAV",
                HostVersion = "3.1.1"
            });

            // 受控宿主能力必须在模块 OnLoad 之前注册，否则框架在启动期拿不到。
            // 窗口要等 Run() 之后才建，所以 HostControl 用延迟绑定：先注册、后 Attach。
            var hostControl = new HostControl(_host);
            _host.Services.Register<Pcv.Modular.Contracts.IHostControl>(hostControl);

            ModuleLoadReport report;
            try
            {
                report = _host.Run();
            }
            catch (Exception ex)
            {
                MessageBox.Show("模块加载过程异常终止：\n" + ex, "PAINCHARNWAV", MessageBoxButton.OK, MessageBoxImage.Error);
                Shutdown(1);
                return;
            }

            // TRM 外观必须赶在第一个窗口构造前：XAML 里的 DynamicResource
            // 要在窗口加载时就解析到正确的一套令牌。--dark 供离屏渲染截图用。
            _host.Services.TryGet(out Pcv.Modular.Contracts.ISettingsService trmSettings);
            TrmThemeService.Initialize(trmSettings);
            if (Array.IndexOf(e.Args, "--dark") >= 0) TrmThemeService.Apply(true);

            // 主题必须赶在窗口构造前应用：MainWindow 的构造函数会把当前主题读进下拉框与顶栏渐变
            var themeIndex = Array.IndexOf(e.Args, "--theme");
            if (themeIndex >= 0 && themeIndex + 1 < e.Args.Length &&
                _host.Services.TryGet(out Pcv.Modular.Contracts.IThemeService themeService))
            {
                themeService.Apply(e.Args[themeIndex + 1]);
            }

            // 一键自检：命令行直接开这个窗口。界面里走侧栏那个按钮，两者同一个窗口。
            // 配 --render 可以离屏渲染成 PNG，用来确认新窗口真的画出来了。
            if (Array.IndexOf(e.Args, "--selfcheck") >= 0)
            {
                var checkWindow = new SelfCheckWindow(report, modulesDirectory,
                                                      baseDirectory, _host.Host.Version);
                MainWindow = checkWindow;
                var checkRender = Array.IndexOf(e.Args, "--render");
                if (checkRender >= 0)
                {
                    var target = checkRender + 1 < e.Args.Length
                        ? e.Args[checkRender + 1]
                        : Path.Combine(baseDirectory, "selfcheck.png");
                    try
                    {
                        RenderToFile(checkWindow, target, 780, 620);
                        Console.WriteLine("已渲染：" + target);
                        Shutdown(0);
                    }
                    catch (Exception ex)
                    {
                        Console.WriteLine("渲染失败：" + ex);
                        Shutdown(2);
                    }
                    return;
                }
                checkWindow.Show();
                return;
            }

            var window = new MainWindow(_host, report);
            MainWindow = window;
            hostControl.Attach(window);

            // 给离屏渲染用：--view add 直接停在"加一个包"那一页
            var viewIndex = Array.IndexOf(e.Args, "--view");
            if (viewIndex >= 0 && viewIndex + 1 < e.Args.Length && e.Args[viewIndex + 1] == "add")
                window.ShowAddViewForRender();

            if (Array.IndexOf(e.Args, "--expand") >= 0)
                window.ExpandFrameworksForRender();

            var pageIndex = Array.IndexOf(e.Args, "--page");
            if (pageIndex >= 0 && pageIndex + 1 < e.Args.Length)
            {
                var page = e.Args[pageIndex + 1];
                window.ShowViewForRender(page);
                // 市场页是异步拉的，离屏渲染等不到；这里同步补一次，截图才有真实条目
                if (page == "market") window.LoadMarketForRender();
                // --community 让市场走 GitHub 源，验证社区搜索这条路
                if (page == "market" && Array.IndexOf(e.Args, "--community") >= 0)
                {
                    var keywordIndex = Array.IndexOf(e.Args, "--keyword");
                    var keyword = keywordIndex >= 0 && keywordIndex + 1 < e.Args.Length
                        ? e.Args[keywordIndex + 1] : null;
                    window.LoadCommunityForRender(keyword);
                }
            }

            // 自动化验证：窗口构造完、XAML 已经解析过之后再换一次主题。
            // 若界面误写成 StaticResource，这一下换不动，截图就会露馅。
            if (Array.IndexOf(e.Args, "--switch-dark") >= 0)
                TrmThemeService.Apply(true);

            // 离屏渲染：给自动化验证用，不弹窗、不需要人盯着，也能确认界面真的画出来了
            var renderIndex = Array.IndexOf(e.Args, "--render");
            if (renderIndex >= 0)
            {
                var target = renderIndex + 1 < e.Args.Length
                    ? e.Args[renderIndex + 1]
                    : Path.Combine(baseDirectory, "preview.png");
                var width = 1080;
                var height = 720;
                var sizeIndex = Array.IndexOf(e.Args, "--size");
                if (sizeIndex >= 0 && sizeIndex + 1 < e.Args.Length)
                {
                    var parts = e.Args[sizeIndex + 1].Split('x');
                    if (parts.Length == 2 && int.TryParse(parts[0], out var w) && int.TryParse(parts[1], out var h))
                    {
                        width = w;
                        height = h;
                    }
                }
                try
                {
                    RenderToFile(window, target, width, height);
                    Console.WriteLine("已渲染：" + target);
                    Shutdown(0);
                }
                catch (Exception ex)
                {
                    Console.WriteLine("渲染失败：" + ex);
                    Shutdown(2);
                }
                return;
            }

            _window = window;

            // 启动即显示主界面（默认落在桌面页；初次启动由 MainWindow 停在引导页）。
            // 以前是"先 Show 再 Hide 缩进托盘"，结果用户以为软件没开。
            // 想回到后台常驻就带 --tray；--show 保留兼容旧的截图/调试命令。
            window.Show();
            SetupTray();

            var trayOnly = Array.IndexOf(e.Args, "--tray") >= 0;
            if (trayOnly) window.Hide();

            AutoStartFrameworks(window);

            var openIndex = Array.IndexOf(e.Args, "--open");
            if (openIndex >= 0 && openIndex + 1 < e.Args.Length)
                window.OpenModuleById(e.Args[openIndex + 1]);
        }

        private static void RenderToFile(Window window, string path, int width, int height)
        {
            var content = (FrameworkElement)window.Content;
            window.Content = null;
            content.Width = width;
            content.Height = height;
            content.Measure(new Size(width, height));
            content.Arrange(new Rect(0, 0, width, height));
            content.UpdateLayout();

            var bitmap = new System.Windows.Media.Imaging.RenderTargetBitmap(width, height, 96, 96,
                System.Windows.Media.PixelFormats.Pbgra32);
            bitmap.Render(content);

            var encoder = new System.Windows.Media.Imaging.PngBitmapEncoder();
            encoder.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(bitmap));
            var folder = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(folder)) Directory.CreateDirectory(folder);
            using (var stream = File.Create(path)) encoder.Save(stream);
        }

        /// <summary>
        /// 托盘常驻：主窗口一启动就藏起来，只留图标；双击或点菜单才回到主界面。
        /// "启动后自动隐藏""框架各自一个窗口"两件事都靠这里兜住，不然用户会以为软件没开。
        /// </summary>
        private static void LogCrash(Exception exception)
        {
            if (exception == null) return;
            try
            {
                var folder = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "logs");
                Directory.CreateDirectory(folder);
                File.AppendAllText(Path.Combine(folder, "crash.log"),
                    $"==== {DateTime.Now:yyyy-MM-dd HH:mm:ss} ===={Environment.NewLine}{exception}{Environment.NewLine}");
            }
            catch
            {
                // 连日志都写不了就算了
            }
        }

        private void SetupTray()
        {
            try
            {
                var icon = System.Drawing.Icon.ExtractAssociatedIcon(
                    System.Reflection.Assembly.GetExecutingAssembly().Location);

                var menu = new System.Windows.Forms.ContextMenuStrip();
                menu.Items.Add("显示主界面", null, (s, e) => ShowMainWindow());
                menu.Items.Add("退出", null, (s, e) => Shutdown());

                _tray = new System.Windows.Forms.NotifyIcon
                {
                    Icon = icon,
                    Text = "PAINCHARNWAV（PCV）",
                    Visible = true,
                    ContextMenuStrip = menu
                };
                _tray.DoubleClick += (s, e) => ShowMainWindow();
                _tray.BalloonTipTitle = "PCV 已启动";
                _tray.BalloonTipText = "主界面已经打开；带 --tray 启动才只留托盘图标。";
                // 主窗口本来就显示着，启动再弹气泡反而吵，这里不弹。
            }
            catch (Exception ex)
            {
                Console.WriteLine("托盘图标创建失败：" + ex.Message);
                // 托盘起不来就不能藏窗口，不然用户找不着程序
                _window?.Show();
            }
        }

        private void ShowMainWindow()
        {
            if (_window == null) return;
            _window.Show();
            if (_window.WindowState == WindowState.Minimized) _window.WindowState = WindowState.Normal;
            _window.Activate();
        }

        /// <summary>清单里写了 autoStart 的进程型框架，开机就拉起来，主窗口继续躲着。</summary>
        private void AutoStartFrameworks(MainWindow window)
        {
            try
            {
                var modules = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "modules");
                if (!Directory.Exists(modules)) return;

                foreach (var directory in Directory.GetDirectories(modules))
                {
                    var manifest = ModuleHostAccessor.ReadManifest(directory);
                    if (manifest == null || !manifest.AutoStart) continue;
                    if (!string.Equals(manifest.Kind, "process", StringComparison.OrdinalIgnoreCase)) continue;

                    try
                    {
                        window.RunProcessFrameworkFor(manifest, directory);
                    }
                    catch (Exception ex)
                    {
                        _host.Log.Error($"自动启动 {manifest.Id} 失败", ex);
                    }
                }
            }
            catch (Exception ex)
            {
                _host.Log.Error("自动启动框架时出错", ex);
            }
        }

        protected override void OnExit(ExitEventArgs e)
        {
            if (_tray != null)
            {
                _tray.Visible = false;
                _tray.Dispose();
                _tray = null;
            }
            _host?.Dispose();
            base.OnExit(e);
        }
    }
}
