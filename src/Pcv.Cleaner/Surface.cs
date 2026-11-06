using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Pcv.Modular.Abstractions;
using Pcv.Modular.Contracts;

namespace Pcv.Cleaner
{
    /// <summary>清理的界面：先告诉你"能清多少"，你点头才动手。</summary>
    public sealed partial class Entry : IFrameworkSurface
    {
        public string Title => "垃圾清理";

        public object CreateView(IModuleContext context)
        {
            var root = new StackPanel();

            var countText = new TextBlock
            {
                FontSize = 26,
                FontWeight = FontWeights.Bold,
                Foreground = new SolidColorBrush(Color.FromRgb(0x13, 0x70, 0xF3))
            };
            var sizeText = new TextBlock
            {
                FontSize = 12.5,
                Margin = new Thickness(0, 4, 0, 0),
                Foreground = new SolidColorBrush(Color.FromRgb(0x7C, 0x87, 0x98))
            };

            void Refresh()
            {
                var survey = Survey();
                countText.Text = survey.Item1 + " 项";
                sizeText.Text = survey.Item1 == 0
                    ? "没有过期的东西，不用清"
                    : $"合计 {survey.Item2 / 1024.0 / 1024:0.##} MB";
            }
            Refresh();

            var head = new StackPanel { Orientation = Orientation.Horizontal };
            head.Children.Add(countText);
            root.Children.Add(head);
            root.Children.Add(sizeText);

            root.Children.Add(Section("清什么"));
            root.Children.Add(Body("· 超过 3 天的 .trash（卸掉的东西的回收站）\n" +
                                   "· 超过 7 天的 .backup（覆盖安装留下的旧版本）\n" +
                                   "· 超过 14 天的日志（modules.log 不动）"));

            var status = new TextBlock
            {
                Text = "",
                FontSize = 12,
                Margin = new Thickness(0, 14, 0, 0),
                TextWrapping = TextWrapping.Wrap,
                Foreground = new SolidColorBrush(Color.FromRgb(0x1B, 0x6B, 0x45))
            };
            root.Children.Add(status);

            var button = new Button
            {
                Content = "现在清一次",
                HorizontalAlignment = HorizontalAlignment.Left,
                Margin = new Thickness(0, 16, 0, 0),
                Padding = new Thickness(14, 6, 14, 6),
                Cursor = System.Windows.Input.Cursors.Hand
            };
            button.Click += (s, e) =>
            {
                var result = CleanNow();
                status.Text = result.Item2.Count == 0
                    ? "清完了，本来就没东西可清。"
                    : $"清掉 {result.Item2.Count} 项，腾出 {result.Item1 / 1024.0 / 1024:0.##} MB。";
                context.Log.Info("界面里手动清了一次：" + status.Text);
                Refresh();
            };
            root.Children.Add(button);

            return root;
        }

        private static FrameworkElement Section(string text) => new TextBlock
        {
            Text = text,
            FontSize = 11.5,
            FontWeight = FontWeights.SemiBold,
            Foreground = new SolidColorBrush(Color.FromRgb(0x8A, 0x94, 0xA3)),
            Margin = new Thickness(0, 22, 0, 6)
        };

        private static FrameworkElement Body(string text) => new TextBlock
        {
            Text = text,
            FontSize = 12,
            TextWrapping = TextWrapping.Wrap,
            LineHeight = 20,
            Foreground = new SolidColorBrush(Color.FromRgb(0x4A, 0x55, 0x63))
        };
    }
}
