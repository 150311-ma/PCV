using System;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Pcv.Modular.Abstractions;
using Pcv.Modular.Contracts;

namespace Pcv.Official.Notes
{
    /// <summary>
    /// 随手记。整个包只有一个文本框：写到自己的 DataDirectory，
    /// 不碰别人的文件，也不联网。用来演示「运行空间隔离」这句话到底是什么意思。
    /// </summary>
    public sealed class Entry : IPclModule, IFrameworkSurface
    {
        private string _file;

        public string Title => "随手记";

        public void OnLoad(IModuleContext context)
        {
            _file = Path.Combine(context.DataDirectory, "note.txt");
            Directory.CreateDirectory(context.DataDirectory);
        }

        public void OnStart() { }
        public void OnStop() { }

        public object CreateView(IModuleContext context)
        {
            var root = new StackPanel();

            root.Children.Add(new TextBlock
            {
                Text = "写点什么，关掉窗口也不会丢",
                FontSize = 15,
                FontWeight = FontWeights.SemiBold,
                Foreground = new SolidColorBrush(Color.FromRgb(0x13, 0x1A, 0x24))
            });

            var box = new TextBox
            {
                AcceptsReturn = true,
                AcceptsTab = true,
                TextWrapping = TextWrapping.Wrap,
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                Height = 210,
                Padding = new Thickness(12),
                FontSize = 13,
                Margin = new Thickness(0, 12, 0, 0),
                BorderBrush = new SolidColorBrush(Color.FromRgb(0xD7, 0xE3, 0xF4)),
                BorderThickness = new Thickness(1),
                Background = new SolidColorBrush(Color.FromRgb(0xFB, 0xFC, 0xFE))
            };
            try { box.Text = File.Exists(_file) ? File.ReadAllText(_file) : ""; }
            catch (Exception ex) { context.Log.Warn("便签读不出来：" + ex.Message); }
            root.Children.Add(box);

            var row = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 10, 0, 0) };
            var save = new Button
            {
                Content = "存一下",
                Padding = new Thickness(15, 6, 15, 6),
                Background = new SolidColorBrush(Color.FromRgb(0x13, 0x70, 0xF3)),
                Foreground = Brushes.White,
                BorderThickness = new Thickness(0),
                Cursor = System.Windows.Input.Cursors.Hand,
                FontSize = 12
            };
            var hint = new TextBlock
            {
                Text = "",
                FontSize = 11.5,
                Foreground = new SolidColorBrush(Color.FromRgb(0x0E, 0x9F, 0x6E)),
                Margin = new Thickness(12, 0, 0, 0),
                VerticalAlignment = VerticalAlignment.Center
            };
            save.Click += (s, e) =>
            {
                try
                {
                    File.WriteAllText(_file, box.Text);
                    hint.Text = "存好了";
                    context.Log.Info("便签已保存");
                }
                catch (Exception ex)
                {
                    hint.Text = "没存上：" + ex.Message;
                    context.Log.Error("便签保存失败", ex);
                }
            };
            row.Children.Add(save);
            row.Children.Add(hint);
            root.Children.Add(row);

            root.Children.Add(new TextBlock
            {
                Text = "文件在 " + _file,
                FontSize = 10.5,
                Foreground = new SolidColorBrush(Color.FromRgb(0x9A, 0xA4, 0xB2)),
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 14, 0, 0)
            });

            return root;
        }
    }
}
