using System.Collections.Generic;
using Pcv.Modular.Contracts;
using Pcv.Core;

namespace Pcv.Theme
{
    /// <summary>
    /// 由一组 HSL 参数算出整套颜色令牌。
    ///
    /// 令牌名沿用 ColorBrush1~8 / ColorBrushGray1~8 / ColorObject* / ColorBrushBg*，
    /// 这样 XAML 里的 {DynamicResource ColorBrush3} 一行都不用改。
    /// </summary>
    public sealed class Palette
    {
        public Palette(ThemeInfo theme)
        {
            Theme = theme;
            Tokens = new Dictionary<string, Rgb>();
            Rebuild();
        }

        public ThemeInfo Theme { get; }
        public Dictionary<string, Rgb> Tokens { get; }

        private void Rebuild()
        {
            var hue = Theme.Hue;
            var saturation = Theme.Saturation;
            var adjust = Theme.LightAdjust;

            var c1 = Rgb.FromHslCorrected(hue, saturation * 0.2, 25 + adjust * 0.3);
            var c2 = Rgb.FromHslCorrected(hue, saturation, 45 + adjust);
            var c3 = Rgb.FromHslCorrected(hue, saturation, 55 + adjust);
            var c4 = Rgb.FromHslCorrected(hue, saturation, 65 + adjust);
            var c5 = Rgb.FromHslCorrected(hue, saturation, 80 + adjust * 0.4);
            var c6 = Rgb.FromHslCorrected(hue, saturation, 91 + adjust * 0.1);
            var c7 = Rgb.FromHslCorrected(hue, saturation, 95);
            var c8 = Rgb.FromHslCorrected(hue, saturation, 97);

            var g1 = Rgb.Gray(64);
            var g2 = Rgb.Gray(115);
            var g3 = Rgb.Gray(140);
            var g4 = Rgb.Gray(166);
            var g5 = Rgb.Gray(204);
            var g6 = Rgb.Gray(235);
            var g7 = Rgb.Gray(240);
            var g8 = Rgb.Gray(245);

            // 背景不是纯色：主色四档 + 一档灰压一下，才不会整片蓝
            var background0 = c4 * 0.4 + c5 * 0.4 + g4 * 0.2;
            var background1 = Rgb.Blend(Rgb.Gray(190), c7, 0.5);

            Tokens.Clear();
            for (var level = 1; level <= 8; level++)
            {
                var color = new[] { c1, c2, c3, c4, c5, c6, c7, c8 }[level - 1];
                Tokens["ColorBrush" + level] = color;
                Tokens["ColorObject" + level] = color;
                Tokens["ColorBrushGray" + level] = new[] { g1, g2, g3, g4, g5, g6, g7, g8 }[level - 1];
            }

            Tokens["ColorBrushBg0"] = background0;
            Tokens["ColorBrushBg1"] = background1;
            Tokens["ColorObjectBg0"] = background0;
            Tokens["ColorObjectBg1"] = background1;
        }

        /// <summary>顶栏渐变的三段色标（左、中、右）。TopbarDelta 为 0 时就是纯色。</summary>
        public IReadOnlyList<ThemeGradientStop> TopbarGradient()
        {
            var delta = Theme.TopbarDelta;
            if (delta < 0.01 && delta > -0.01)
            {
                var solid = Rgb.FromHslCorrected(Theme.Hue, Theme.Saturation, 55 + Theme.LightAdjust).ToHex();
                return new List<ThemeGradientStop>
                {
                    new ThemeGradientStop { Color = solid, Offset = 0 },
                    new ThemeGradientStop { Color = solid, Offset = 1 }
                };
            }

            return new List<ThemeGradientStop>
            {
                new ThemeGradientStop
                {
                    Color = Rgb.FromHslCorrected(Theme.Hue - delta, Theme.Saturation, 62 + Theme.LightAdjust).ToHex(),
                    Offset = 0
                },
                new ThemeGradientStop
                {
                    Color = Rgb.FromHslCorrected(Theme.Hue, Theme.Saturation, 55 + Theme.LightAdjust).ToHex(),
                    Offset = 0.5
                },
                new ThemeGradientStop
                {
                    Color = Rgb.FromHslCorrected(Theme.Hue + delta, Theme.Saturation, 62 + Theme.LightAdjust).ToHex(),
                    Offset = 1
                }
            };
        }

        public string GetHex(string token)
        {
            return Tokens.TryGetValue(token, out var color) ? color.ToHex() : "#000000";
        }
    }
}
