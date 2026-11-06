using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Media;
using Pcv.Modular.Abstractions;
using Pcv.Modular.Contracts;
using Pcv.Core;

namespace Pcv.Theme
{
    /// <summary>
    /// 主题服务：算颜色 → 写进 Application.Resources → 抛事件。
    ///
    /// 刻意不认识任何窗口类型：顶栏渐变由订阅方自己画，
    /// 主题模块不需要知道宿主长什么样。
    /// </summary>
    public sealed class ThemeService : IThemeService
    {
        private readonly ILogger _log;
        private readonly ISettingsService _settings;
        private readonly List<ThemeInfo> _themes = Presets.CreateAll();
        private ThemeInfo _current;
        private Palette _palette;

        public ThemeService(ILogger log, ISettingsService settings)
        {
            _log = log;
            _settings = settings;
            _current = _themes[0];
            _palette = new Palette(_current);
        }

        public IReadOnlyList<ThemeInfo> Themes => _themes;
        public ThemeInfo Current => _current;
        public Palette CurrentPalette => _palette;

        public event Action<ThemeInfo> ThemeChanged;

        public bool Apply(string themeId)
        {
            var target = _themes.Find(t => string.Equals(t.Id, themeId, StringComparison.OrdinalIgnoreCase));
            if (target == null)
            {
                _log.Warn($"没有 {themeId} 这个主题，保持 {_current.Id}");
                return false;
            }

            _current = target;
            _palette = new Palette(target);

            var application = Application.Current;
            if (application == null)
            {
                // 命令行/无界面进程不算失败：颜色算好了，等窗口起来再写
                _log.Info($"主题切到 {target.Name}（当前没有 Application，只算颜色不写资源）");
                ThemeChanged?.Invoke(target);
                return true;
            }

            void Write()
            {
                foreach (var pair in _palette.Tokens)
                {
                    var color = Color.FromRgb(Rgb.ToByte(pair.Value.R), Rgb.ToByte(pair.Value.G), Rgb.ToByte(pair.Value.B));

                    if (pair.Key.StartsWith("ColorObject", StringComparison.Ordinal))
                    {
                        application.Resources[pair.Key] = color;
                    }
                    else
                    {
                        var brush = new SolidColorBrush(color);
                        brush.Freeze();
                        application.Resources[pair.Key] = brush;
                    }
                }
            }

            if (application.Dispatcher.CheckAccess()) Write();
            else application.Dispatcher.Invoke(Write);

            _settings?.SetValue("UiThemeId", target.Id);
            _log.Info($"主题切到 {target.Name}（色相 {target.Hue}，亮度偏移 {target.LightAdjust}）");
            ThemeChanged?.Invoke(target);
            return true;
        }

        public string GetColor(string token) => _palette.GetHex(token);

        public IReadOnlyList<ThemeGradientStop> GetTopbarGradient() => _palette.TopbarGradient();
    }
}
