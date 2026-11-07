using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Pcv.Modular.Abstractions;
using Pcv.Modular.Contracts;

namespace Pcv.Official.Dock
{
    /// <summary>
    /// 快捷面板：把 IHostControl 开放的宿主能力摊开给用户看。
    ///
    /// 框架此前只能被宿主调用；现在它能读模块清单、打开别的模块、刷新列表、
    /// 切页面、在状态栏说话 —— 但拿不到窗口对象，也改不了别人的界面。
    /// 这个包就是那份「能力清单」的可执行说明。
    /// </summary>
    public sealed class Entry : IPclModule, IFrameworkSurface
    {
        private IModuleContext _context;
        private IHostControl _host;

        public string Title => "快捷面板";

        public void OnLoad(IModuleContext context)
        {
            _context = context;
            context.Services.TryGet(out _host);
        }

        public void OnStart() { }
        public void OnStop() { }

        public object CreateView(IModuleContext context)
        {
            var root = new StackPanel();

            root.Children.Add(new TextBlock
            {
                Text = _host == null ? "这个宿主没有开放 IHostControl" : "宿主开放的能力",
                FontSize = 15,
                FontWeight = FontWeights.SemiBold,
                Foreground = new SolidColorBrush(Color.FromRgb(0x13, 0x1A, 0x24))
            });
            root.Children.Add(new TextBlock
            {
                Text = _host == null
                    ? "装在支持框架权限的 PCV 上才能用这块面板。"
                    : "PCV " + _host.HostVersion + " · 下面这些操作都由宿主代做，框架碰不到窗口本身。",
                FontSize = 11.5,
                TextWrapping = TextWrapping.Wrap,
                Foreground = new SolidColorBrush(Color.FromRgb(0x7A, 0x86, 0x99)),
                Margin = new Thickness(0, 6, 0, 0)
            });

            if (_host == null) return root;

            var actions = new WrapPanel { Margin = new Thickness(0, 16, 0, 0) };
            actions.Children.Add(Action("刷新模块列表", () => _host.RefreshModules()));
            actions.Children.Add(Action("去市场", () => _host.Navigate("market")));
            actions.Children.Add(Action("去桌面", () => _host.Navigate("home")));
            actions.Children.Add(Action("状态栏报个信", () => { _host.Notify("快捷面板打过招呼"); return true; }));
            root.Children.Add(actions);

            root.Children.Add(new TextBlock
            {
                Text = "已安装的模块（点一下打开）",
                FontSize = 11.5,
                FontWeight = FontWeights.SemiBold,
                Foreground = new SolidColorBrush(Color.FromRgb(0x9A, 0xA4, 0xB2)),
                Margin = new Thickness(0, 22, 0, 8)
            });

            var list = new StackPanel();
            try
            {
                foreach (var module in _host.Modules)
                {
                    var id = module.Id;
                    var button = new Button
                    {
                        Content = $"{module.Name}   {module.Version}   ·   {module.State}",
                        HorizontalContentAlignment = HorizontalAlignment.Left,
                        Padding = new Thickness(12, 8, 12, 8),
                        Margin = new Thickness(0, 0, 0, 6),
                        Background = new SolidColorBrush(Color.FromRgb(0xF6, 0xF8, 0xFC)),
                        BorderThickness = new Thickness(0),
                        Foreground = new SolidColorBrush(Color.FromRgb(0x4A, 0x55, 0x63)),
                        FontSize = 12,
                        Cursor = System.Windows.Input.Cursors.Hand
                    };
                    button.Click += (s, e) =>
                    {
                        if (!_host.OpenModule(id)) _context?.Log.Warn("打不开：" + id);
                    };
                    list.Children.Add(button);
                }
                if (list.Children.Count == 0)
                    list.Children.Add(Hint("还没装模块。"));
            }
            catch (Exception ex)
            {
                list.Children.Add(Hint("读模块清单失败：" + ex.Message));
            }
            root.Children.Add(list);

            return root;
        }

        private static Button Action(string text, Func<bool> run)
        {
            var button = new Button
            {
                Content = text,
                Margin = new Thickness(0, 0, 8, 8),
                Padding = new Thickness(14, 7, 14, 7),
                Background = new SolidColorBrush(Color.FromRgb(0xEA, 0xF2, 0xFE)),
                Foreground = new SolidColorBrush(Color.FromRgb(0x0F, 0x63, 0xDB)),
                BorderThickness = new Thickness(0),
                Cursor = System.Windows.Input.Cursors.Hand,
                FontSize = 12
            };
            button.Click += (s, e) => { try { run(); } catch { /* 宿主不答应就算了 */ } };
            return button;
        }

        private static FrameworkElement Hint(string text) => new TextBlock
        {
            Text = text,
            FontSize = 12,
            TextWrapping = TextWrapping.Wrap,
            Foreground = new SolidColorBrush(Color.FromRgb(0x9A, 0xA4, 0xB2))
        };
    }
}
