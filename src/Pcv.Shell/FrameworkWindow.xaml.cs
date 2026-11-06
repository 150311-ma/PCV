using System;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using Pcv.Modular.Abstractions;
using Pcv.Modular.Contracts;
using Pcv.Modular.Host;

namespace Pcv.Shell
{
    /// <summary>
    /// 点"打开使用它"弹出来的窗口。
    ///
    /// 两种框架：
    ///   · 实现了 IFrameworkSurface 的，把它自己的界面塞进来；
    ///   · 没有界面的（纯后台的），显示它的说明、数据目录和最近的日志——
    ///     总比"打开一片空白"强，至少能看出它到底在干什么。
    /// </summary>
    public partial class FrameworkWindow : Window
    {
        private readonly ModuleEntry _entry;

        public FrameworkWindow(ModuleEntry entry)
        {
            _entry = entry;
            InitializeComponent();

            var name = entry.Manifest?.DisplayName ?? entry.Id;
            LabTitle.Text = name;
            LabMark.Text = (name.Length > 0 ? name.Substring(0, 1) : "P").ToUpperInvariant();
            Title = name + " · PCV";
            LabFooter.Text = $"{entry.Id} {entry.Manifest?.Version}　·　数据目录 {DataDirectory(entry)}";

            Loaded += (s, e) =>
            {
                GlassHelper.TryEnableRoundedCorners(this);
                Host.Content = BuildContent();
            };
        }

        private object BuildContent()
        {
            if (_entry.Instance is IFrameworkSurface surface)
            {
                try
                {
                    var view = surface.CreateView(_entry.Context) as FrameworkElement;
                    if (view != null)
                    {
                        if (!string.IsNullOrEmpty(surface.Title)) LabTitle.Text = surface.Title;
                        return view;
                    }
                }
                catch (Exception ex)
                {
                    return Notice("这个框架的界面没有打开成功", ex.Message);
                }
            }

            return BuildFallback();
        }

        private object BuildFallback()
        {
            var panel = new StackPanel();

            panel.Children.Add(Text("这个框架没有提供界面"));
            panel.Children.Add(Muted("它在后台干活，装好就已经在运行了。下面这些是它的实际情况。"));
            panel.Children.Add(Divider());

            panel.Children.Add(Label("说明"));
            panel.Children.Add(Text(_entry.Manifest?.Description ?? "（没有写说明）"));

            panel.Children.Add(Label("状态"));
            panel.Children.Add(Text(StateText(_entry.State) + (string.IsNullOrEmpty(_entry.Error) ? "" : "：" + _entry.Error)));

            panel.Children.Add(Label("数据目录"));
            panel.Children.Add(Mono(DataDirectory(_entry)));

            panel.Children.Add(Label("最近的日志"));
            panel.Children.Add(Mono(TailLog(_entry.Id)));

            return panel;
        }

        private static string DataDirectory(ModuleEntry entry)
        {
            try
            {
                return entry.Context?.DataDirectory ?? Path.Combine(entry.Manifest?.DirectoryPath ?? "", "data");
            }
            catch
            {
                return "（取不到）";
            }
        }

        /// <summary>从日志里挑出属于这个框架的最后 20 行。</summary>
        private static string TailLog(string moduleId)
        {
            try
            {
                var file = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "logs", "modules.log");
                if (!File.Exists(file)) return "（还没有日志）";

                // net48 没有 TakeLast，自己数尾巴
                var hit = File.ReadAllLines(file)
                    .Where(line => line.IndexOf("[" + moduleId + "]", StringComparison.OrdinalIgnoreCase) >= 0)
                    .ToList();
                var lines = hit.Skip(Math.Max(0, hit.Count - 20)).ToList();

                return lines.Count == 0 ? "（这个框架还没写过日志）" : string.Join(Environment.NewLine, lines);
            }
            catch (Exception ex)
            {
                return "读日志失败：" + ex.Message;
            }
        }

        private static string StateText(ModuleState state)
        {
            switch (state)
            {
                case ModuleState.Started: return "已完成安装，正在运行";
                case ModuleState.Loaded: return "已安装，未启动";
                case ModuleState.Failed: return "安装失败";
                case ModuleState.Skipped: return "被跳过";
                default: return state.ToString();
            }
        }

        // ── 几个小工具：这个窗口不引资源字典，直接手搓，省得跟主界面样式耦合 ──

        private static TextBlock Text(string text) => new TextBlock
        {
            Text = text,
            TextWrapping = TextWrapping.Wrap,
            FontSize = 13,
            Foreground = new SolidColorBrush(Color.FromRgb(0x34, 0x3D, 0x4A)),
            Margin = new Thickness(0, 2, 0, 0)
        };

        private static TextBlock Muted(string text) => new TextBlock
        {
            Text = text,
            TextWrapping = TextWrapping.Wrap,
            FontSize = 12,
            Foreground = new SolidColorBrush(Color.FromRgb(0x7C, 0x87, 0x98)),
            Margin = new Thickness(0, 6, 0, 0)
        };

        private static TextBlock Label(string text) => new TextBlock
        {
            Text = text,
            FontSize = 11.5,
            FontWeight = FontWeights.SemiBold,
            Foreground = new SolidColorBrush(Color.FromRgb(0x8A, 0x94, 0xA3)),
            Margin = new Thickness(0, 18, 0, 4)
        };

        private static TextBlock Mono(string text) => new TextBlock
        {
            Text = text,
            TextWrapping = TextWrapping.Wrap,
            FontFamily = new FontFamily("Consolas, Microsoft YaHei UI"),
            FontSize = 11.5,
            LineHeight = 18,
            Foreground = new SolidColorBrush(Color.FromRgb(0x4A, 0x55, 0x63))
        };

        private static Border Divider() => new Border
        {
            Height = 1,
            Background = new SolidColorBrush(Color.FromRgb(0xEC, 0xEF, 0xF4)),
            Margin = new Thickness(0, 16, 0, 0)
        };

        private static UIElement Notice(string title, string detail)
        {
            var panel = new StackPanel();
            panel.Children.Add(Text(title));
            panel.Children.Add(Muted(detail));
            return panel;
        }

        private void TitleBar_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            if (e.ClickCount == 2)
            {
                WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;
                return;
            }
            if (e.ButtonState == MouseButtonState.Pressed) DragMove();
        }

        private void BtnClose_Click(object sender, RoutedEventArgs e) => Close();
    }
}
