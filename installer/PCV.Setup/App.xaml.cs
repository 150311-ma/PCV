using System;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace PCV.Setup
{
    public partial class App : Application
    {
        public static string[] Args = Array.Empty<string>();

        protected override void OnStartup(StartupEventArgs e)
        {
            base.OnStartup(e);
            Args = e.Args;

            // 装完之后这个 exe 会被复制成 Uninstall.exe，走同一条代码路径
            if (Has("--uninstall"))
            {
                Uninstaller.Run();
                Shutdown(0);
                return;
            }

            // 给已经装好的目录补一个快捷方式（不再复制一份程序）
            if (Has("--shortcut"))
            {
                var link = Value("--shortcut");
                var target = Value("--target");
                if (string.IsNullOrWhiteSpace(link) || string.IsNullOrWhiteSpace(target))
                {
                    Console.WriteLine("用法：--shortcut <lnk 路径> --target <exe 路径>");
                    Shutdown(2);
                    return;
                }

                try
                {
                    Installer.CreateShortcut(link, target, System.IO.Path.GetDirectoryName(target));
                    Console.WriteLine("快捷方式已建立：" + link);
                    Shutdown(0);
                }
                catch (Exception ex)
                {
                    Console.WriteLine("建快捷方式失败：" + ex.Message);
                    Shutdown(3);
                }
                return;
            }

            if (Has("--silent"))
            {
                var target = Value("--dir") ?? Installer.DefaultTarget;
                var result = Installer.Install(target, !Has("--no-shortcuts"), null, !Has("--no-registry"));
                Console.WriteLine(result.Message);
                Shutdown(result.Success ? 0 : 2);
                return;
            }

            // 离屏渲染：会话没渲染桌面时也能出图（截图抓不到窗口就靠它）
            var renderIndex = Array.FindIndex(Args,
                a => string.Equals(a, "--render", StringComparison.OrdinalIgnoreCase));
            if (renderIndex >= 0 && renderIndex + 1 < Args.Length)
            {
                var window = new InstallWindow();
                var content = (FrameworkElement)window.Content;

                // 必须先从窗口上摘下来：挂在没显示的 Window 上，渲染出来是全透明的（试过了）
                window.Content = null;
                content.Width = 660;
                content.Height = 420;
                content.Measure(new Size(660, 420));
                content.Arrange(new Rect(0, 0, 660, 420));
                content.UpdateLayout();

                var bitmap = new RenderTargetBitmap(1320, 840, 192, 192, PixelFormats.Pbgra32);
                bitmap.Render(content);

                var encoder = new PngBitmapEncoder();
                encoder.Frames.Add(BitmapFrame.Create(bitmap));
                using (var stream = File.Create(Args[renderIndex + 1]))
                    encoder.Save(stream);

                Shutdown(0);
                return;
            }

            new InstallWindow().Show();
        }

        public static bool Has(string flag) =>
            Args.Any(a => string.Equals(a, flag, StringComparison.OrdinalIgnoreCase));

        public static string Value(string name)
        {
            var index = Array.FindIndex(Args,
                a => string.Equals(a, name, StringComparison.OrdinalIgnoreCase));
            return index >= 0 && index + 1 < Args.Length ? Args[index + 1] : null;
        }

        public static string LogoPath =>
            "pack://application:,,,/PCV-Setup;component/assets/pcv.png";
    }
}
