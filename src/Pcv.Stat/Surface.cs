using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using Pcv.Modular.Abstractions;
using Pcv.Modular.Contracts;

namespace Pcv.Stat
{
    /// <summary>
    /// 使用统计的界面。数字全在共享的 ISettingsService 里，
    /// 所以别的框架（哪怕是后装的）也能读同一份计数——这就是"数据互通"。
    /// </summary>
    public sealed partial class Entry : IFrameworkSurface
    {
        public string Title => "使用统计";

        public object CreateView(IModuleContext context)
        {
            var root = new StackPanel();

            var startsText = Value();
            var daysText = Value();
            var secondsText = Value();

            Refresh(startsText, daysText, secondsText);

            var numbers = new UniformGrid { Columns = 3, Margin = new Thickness(0, 4, 0, 0) };
            numbers.Children.Add(Figure(startsText, "打开次数"));
            numbers.Children.Add(Figure(daysText, "从第一次用到现在"));
            numbers.Children.Add(Figure(secondsText, "累计使用"));
            root.Children.Add(numbers);

            root.Children.Add(Section("数据在哪"));
            root.Children.Add(Body(_settings == null
                ? "配置服务不可用，这些数字只在本次运行里有效。"
                : @"共享配置 PCLData\settings.json（键名 Stat*）。别的框架也能读同一份，算公共数据。"));

            var reset = new Button
            {
                Content = "把统计清零",
                HorizontalAlignment = HorizontalAlignment.Left,
                Margin = new Thickness(0, 22, 0, 0),
                Padding = new Thickness(14, 6, 14, 6),
                Cursor = System.Windows.Input.Cursors.Hand
            };
            reset.Click += (s, e) =>
            {
                if (MessageBox.Show("确认清零？打开次数和累计时长会从 0 重算。", "使用统计",
                        MessageBoxButton.OKCancel, MessageBoxImage.Question) != MessageBoxResult.OK) return;

                Write(KeyStarts, 0);
                Write(KeySeconds, 0);
                Write(KeyFirst, 0);
                _settings?.Save();
                context.Log.Info("统计已清零");
                Refresh(startsText, daysText, secondsText);
            };
            root.Children.Add(reset);

            return root;
        }

        private void Refresh(TextBlock startsText, TextBlock daysText, TextBlock secondsText)
        {
            var starts = Read(KeyStarts);
            var first = Read(KeyFirst);
            var seconds = Read(KeySeconds);
            var days = first <= 0 ? 0 : Math.Max(0, (Now() - first) / 86400);

            startsText.Text = starts.ToString();
            daysText.Text = days + " 天";
            secondsText.Text = seconds < 3600
                ? (seconds / 60.0).ToString("0") + " 分钟"
                : (seconds / 3600.0).ToString("0.#") + " 小时";
        }

        // ── 小控件 ──

        private static TextBlock Value() => new TextBlock
        {
            FontSize = 26,
            FontWeight = FontWeights.Bold,
            Foreground = new SolidColorBrush(Color.FromRgb(0x13, 0x70, 0xF3))
        };

        private static FrameworkElement Figure(TextBlock value, string caption)
        {
            var stack = new StackPanel { Margin = new Thickness(0, 0, 16, 0) };
            stack.Children.Add(value);
            stack.Children.Add(new TextBlock
            {
                Text = caption,
                FontSize = 11.5,
                Margin = new Thickness(0, 4, 0, 0),
                Foreground = new SolidColorBrush(Color.FromRgb(0x8A, 0x94, 0xA3)),
                TextWrapping = TextWrapping.Wrap
            });
            return stack;
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
            FontSize = 12.5,
            TextWrapping = TextWrapping.Wrap,
            LineHeight = 20,
            Foreground = new SolidColorBrush(Color.FromRgb(0x4A, 0x55, 0x63))
        };
    }
}
