using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Pcv.Modular.Abstractions;
using Pcv.Modular.Contracts;

namespace Pcv.Official.Guide
{
    /// <summary>一张卡片讲清软件、框架、市场分别是什么。</summary>
    public sealed partial class Entry : IFrameworkSurface
    {
        public string Title => "官方入门指南";

        public object CreateView(IModuleContext context)
        {
            var root = new StackPanel();

            root.Children.Add(Lead("把 PCV 想成一部手机"));
            root.Children.Add(Body("PCV 自己不干活，它只负责把东西装进来、摆到桌面上、点一下能打开。"
                                   + "真正的功能都在「包」里，装多少就有多少。"));

            root.Children.Add(Card("软件", "桌面上那些圆角图标",
                "点开就能用的东西。它自己不含框架，能力都是向框架要的。比如一个游戏启动器。", "#1370F3"));
            root.Children.Add(Card("框架", "藏在后面的能力",
                "提供能力、被软件调用的东西，比如主题、登录、统计。一般不直接摆在桌面上。", "#7A5AF8"));
            root.Children.Add(Card("市场", "装东西最快的一条路",
                "官方把做好的包放在市场上，点一下就下载、校验、装好。也能拖 .pclpkg 或自己写脚本。", "#0E9F6E"));

            root.Children.Add(Section("三步上手"));
            root.Children.Add(Body("① 去「市场」挑一个官方包装上；\n"
                                   + "② 回「桌面」点那个新出现的图标；\n"
                                   + "③ 想自己做一个，去「安装」页粘一段 AI 给的脚本。"));

            var buttons = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 20, 0, 0) };
            buttons.Children.Add(Jump("去市场", "market"));
            buttons.Children.Add(Jump("去安装", "install"));
            root.Children.Add(buttons);

            root.Children.Add(new TextBlock
            {
                Text = "上面两个按钮是框架通过 IHostControl 请宿主切的页 —— 这就是「框架能用的宿主能力」。",
                FontSize = 11,
                Foreground = new SolidColorBrush(Color.FromRgb(0x9A, 0xA4, 0xB2)),
                TextWrapping = TextWrapping.Wrap,
                LineHeight = 17,
                Margin = new Thickness(0, 12, 0, 0)
            });

            return root;
        }

        private Button Jump(string text, string view)
        {
            var button = new Button
            {
                Content = text,
                Margin = new Thickness(0, 0, 10, 0),
                Padding = new Thickness(16, 7, 16, 7),
                Background = new SolidColorBrush(Color.FromRgb(0xEA, 0xF2, 0xFE)),
                Foreground = new SolidColorBrush(Color.FromRgb(0x0F, 0x63, 0xDB)),
                BorderThickness = new Thickness(0),
                Cursor = System.Windows.Input.Cursors.Hand,
                FontSize = 12
            };
            button.Click += (s, e) =>
            {
                var ok = Host != null && Host.Navigate(view);
                if (!ok) Context?.Log.Warn("宿主没接受切页请求：" + view);
            };
            return button;
        }

        private static FrameworkElement Lead(string text) => new TextBlock
        {
            Text = text,
            FontSize = 17,
            FontWeight = FontWeights.SemiBold,
            Foreground = new SolidColorBrush(Color.FromRgb(0x13, 0x1A, 0x24)),
            Margin = new Thickness(0, 0, 0, 8)
        };

        private static FrameworkElement Section(string text) => new TextBlock
        {
            Text = text,
            FontSize = 11.5,
            FontWeight = FontWeights.SemiBold,
            Foreground = new SolidColorBrush(Color.FromRgb(0x9A, 0xA4, 0xB2)),
            Margin = new Thickness(0, 22, 0, 6)
        };

        private static FrameworkElement Body(string text) => new TextBlock
        {
            Text = text,
            FontSize = 12.5,
            TextWrapping = TextWrapping.Wrap,
            LineHeight = 21,
            Foreground = new SolidColorBrush(Color.FromRgb(0x4A, 0x55, 0x63))
        };

        /// <summary>一张概念卡：左边一条色带，右边标题 + 说明。</summary>
        private static FrameworkElement Card(string name, string tagline, string detail, string accent)
        {
            var color = (Color)ColorConverter.ConvertFromString(accent);
            var grid = new Grid { Margin = new Thickness(0, 14, 0, 0) };
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(4) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

            var stripe = new Border
            {
                Background = new SolidColorBrush(color),
                CornerRadius = new CornerRadius(2),
                Width = 4
            };
            Grid.SetColumn(stripe, 0);
            grid.Children.Add(stripe);

            var panel = new StackPanel { Margin = new Thickness(14, 0, 0, 0) };
            var title = new StackPanel { Orientation = Orientation.Horizontal };
            title.Children.Add(new TextBlock
            {
                Text = name,
                FontSize = 13.5,
                FontWeight = FontWeights.SemiBold,
                Foreground = new SolidColorBrush(Color.FromRgb(0x13, 0x1A, 0x24))
            });
            title.Children.Add(new TextBlock
            {
                Text = tagline,
                FontSize = 11.5,
                Foreground = new SolidColorBrush(color),
                Margin = new Thickness(10, 2, 0, 0)
            });
            panel.Children.Add(title);
            panel.Children.Add(new TextBlock
            {
                Text = detail,
                FontSize = 12,
                TextWrapping = TextWrapping.Wrap,
                LineHeight = 19,
                Foreground = new SolidColorBrush(Color.FromRgb(0x6B, 0x76, 0x88)),
                Margin = new Thickness(0, 5, 0, 0)
            });
            Grid.SetColumn(panel, 1);
            grid.Children.Add(panel);
            return grid;
        }
    }
}
