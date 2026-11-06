using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Pcv.Modular.Abstractions;
using Pcv.Modular.Contracts;

namespace Pcv.Diagnostics
{
    /// <summary>自检的界面：把最近一次的报告摊开，随时能重跑一次。</summary>
    public sealed partial class Entry : IFrameworkSurface
    {
        public string Title => "环境自检";

        public object CreateView(IModuleContext context)
        {
            var root = new StackPanel();

            var hint = new TextBlock
            {
                Text = "出问题的时候把这段发出去就行，不用再问你什么系统、什么配置。",
                FontSize = 12,
                TextWrapping = TextWrapping.Wrap,
                Foreground = new SolidColorBrush(Color.FromRgb(0x7C, 0x87, 0x98))
            };
            root.Children.Add(hint);

            var report = new TextBlock
            {
                FontFamily = new FontFamily("Consolas, Microsoft YaHei UI"),
                FontSize = 11.5,
                LineHeight = 19,
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 14, 0, 0),
                Foreground = new SolidColorBrush(Color.FromRgb(0x4A, 0x55, 0x63)),
                Text = Collect()
            };
            root.Children.Add(report);

            var path = new TextBlock
            {
                Text = "报告已经存进 logs\\diagnostics-*.txt",
                FontSize = 11,
                Margin = new Thickness(0, 14, 0, 0),
                Foreground = new SolidColorBrush(Color.FromRgb(0x8A, 0x94, 0xA3)),
                TextWrapping = TextWrapping.Wrap
            };
            root.Children.Add(path);

            var again = new Button
            {
                Content = "重新自检",
                HorizontalAlignment = HorizontalAlignment.Left,
                Margin = new Thickness(0, 16, 0, 0),
                Padding = new Thickness(14, 6, 14, 6),
                Cursor = System.Windows.Input.Cursors.Hand
            };
            again.Click += (s, e) =>
            {
                try
                {
                    RunAndSave(out var file);
                    report.Text = Collect();
                    path.Text = "刚跑完，新报告：" + file;
                    context.Log.Info("界面里重新自检了一次");
                }
                catch (Exception ex)
                {
                    path.Text = "重跑失败：" + ex.Message;
                }
            };
            root.Children.Add(again);

            return root;
        }
    }
}
