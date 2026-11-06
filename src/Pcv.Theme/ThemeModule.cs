using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using Pcv.Modular.Abstractions;
using Pcv.Modular.Contracts;

namespace Pcv.Theme
{
    /// <summary>
    /// 主题模块：装载时把上次选的主题应用回去；界面里能改。
    /// </summary>
    public sealed class ThemeModule : IPclModule, IFrameworkSurface
    {
        private ILogger _log;
        private ISettingsService _settings;
        private ThemeService _service;

        public string Title => "主题";

        public void OnLoad(IModuleContext context)
        {
            _log = context.Log;

            ISettingsService settings = null;
            context.Services.TryGet(out settings);
            _settings = settings;

            _service = new ThemeService(_log, _settings);
            context.Services.Register<IThemeService>(_service);

            var saved = _settings?.GetValue("UiThemeId", Presets.Default.Id) ?? Presets.Default.Id;
            _service.Apply(saved);
        }

        public void OnStart() { }

        public void OnStop()
        {
            _settings?.Save();
        }

        public object CreateView(IModuleContext context)
        {
            var root = new StackPanel();

            var status = new TextBlock
            {
                Text = "当前：" + (_service?.Current.Name ?? "未知"),
                FontSize = 12,
                Margin = new Thickness(0, 0, 0, 16),
                Foreground = new SolidColorBrush(Color.FromRgb(0x7C, 0x87, 0x98))
            };
            root.Children.Add(status);

            var grid = new UniformGrid { Columns = 4 };

            foreach (var theme in _service.Themes)
            {
                var palette = new Palette(theme);
                var preview = new Border
                {
                    Height = 44,
                    CornerRadius = new CornerRadius(8),
                    Background = new LinearGradientBrush(
                        ToColor(palette.GetHex("ColorBrush4")), ToColor(palette.GetHex("ColorBrush2")), 45)
                };

                var panel = new StackPanel { Margin = new Thickness(0, 0, 0, 6) };
                panel.Children.Add(preview);
                panel.Children.Add(new TextBlock
                {
                    Text = theme.Name,
                    FontSize = 12,
                    Margin = new Thickness(2, 6, 0, 0),
                    Foreground = new SolidColorBrush(Color.FromRgb(0x4A, 0x55, 0x63))
                });

                var card = new Border
                {
                    Child = panel,
                    Tag = theme.Id,
                    Padding = new Thickness(6),
                    CornerRadius = new CornerRadius(12),
                    Background = Brushes.Transparent,
                    Cursor = Cursors.Hand,
                    Margin = new Thickness(0, 0, 0, 0)
                };
                card.MouseEnter += (sender, args) =>
                    ((Border)sender).Background = new SolidColorBrush(Color.FromArgb(0x12, 0, 0, 0));
                card.MouseLeave += (sender, args) =>
                    ((Border)sender).Background = Brushes.Transparent;
                card.MouseLeftButtonUp += (sender, args) =>
                {
                    var id = (string)((Border)sender).Tag;
                    if (!_service.Apply(id)) return;
                    status.Text = "当前：" + _service.Current.Name;
                    context.Log.Info("界面里换主题：" + id);
                };

                grid.Children.Add(card);
            }

            root.Children.Add(grid);
            root.Children.Add(new TextBlock
            {
                Text = "点一下立刻生效，会记住，下次打开还是这套。",
                FontSize = 12,
                Margin = new Thickness(0, 14, 0, 0),
                TextWrapping = TextWrapping.Wrap,
                Foreground = new SolidColorBrush(Color.FromRgb(0x8A, 0x94, 0xA3))
            });

            return root;
        }

        private static Color ToColor(string hex)
        {
            try
            {
                return (Color)ColorConverter.ConvertFromString(hex);
            }
            catch
            {
                return Colors.Transparent;
            }
        }
    }
}
