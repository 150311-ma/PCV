using System;

namespace Pcv.Core
{
    /// <summary>
    /// RGB 颜色 + HSL 换算。
    ///
    /// 这里的 HSL 不是课本上那个：段内亮度中心做过修正（见 <see cref="FromHslCorrected"/>），
    /// 目的是让「同一个饱和度下，不同色相的明暗看起来一致」——
    /// 黄色和蓝色用同一个 L，肉眼感受差很多。这套修正是从上游颜色表反推出来的，
    /// 保留它，换主题时观感才和以前一样。
    /// </summary>
    public readonly struct Rgb
    {
        private static readonly double[] Centers =
        {
            0.10, -0.06, -0.30,
            -0.19, -0.15, -0.24,
            -0.32, -0.09, 0.18,
            0.05, -0.12, -0.02,
            0.10, -0.06
        };

        public Rgb(double r, double g, double b)
        {
            R = Clamp(r);
            G = Clamp(g);
            B = Clamp(b);
        }

        public Rgb(byte r, byte g, byte b) : this((double)r, g, b)
        {
        }

        public double R { get; }
        public double G { get; }
        public double B { get; }

        public static Rgb Gray(byte value) => new Rgb(value, value, value);

        /// <summary>#RRGGBB 十六进制，界面直接用。</summary>
        public string ToHex() =>
            "#" + ToByte(R).ToString("X2") + ToByte(G).ToString("X2") + ToByte(B).ToString("X2");

        public static Rgb operator +(Rgb a, Rgb b) => new Rgb(a.R + b.R, a.G + b.G, a.B + b.B);
        public static Rgb operator *(Rgb a, double factor) => new Rgb(a.R * factor, a.G * factor, a.B * factor);

        /// <summary>按比例混合两个颜色，percent 取 0~1。</summary>
        public static Rgb Blend(Rgb a, Rgb b, double percent) =>
            new Rgb(a.R + (b.R - a.R) * percent, a.G + (b.G - a.G) * percent, a.B + (b.B - a.B) * percent);

        /// <summary>
        /// 带段内修正的 HSL：hue 0~360，saturation 0~100，lightness 0~100。
        /// </summary>
        public static Rgb FromHslCorrected(double hue, double saturation, double lightness)
        {
            if (Math.Abs(saturation) < double.Epsilon) return Gray(ToByte(lightness * 2.55));

            hue = (hue + 3_600_000) % 360;
            var center = hue / 30.0;
            var segment = (int)Math.Floor(center);
            if (segment >= Centers.Length - 1) segment = Centers.Length - 2;
            if (segment < 0) segment = 0;

            // 相邻两段的修正系数插值，再按饱和度缩放到"亮度中心"
            var adjusted = 50 - ((1 - center + segment) * Centers[segment]
                                 + (center - segment) * Centers[segment + 1]) * saturation;
            var mapped = (lightness < adjusted
                ? lightness / adjusted
                : 1 + (lightness - adjusted) / (100 - adjusted)) * 50;

            return FromHsl(hue, saturation, mapped);
        }

        /// <summary>标准 HSL → RGB。</summary>
        public static Rgb FromHsl(double hue, double saturation, double lightness)
        {
            if (Math.Abs(saturation) < double.Epsilon) return Gray(ToByte(lightness * 2.55));

            var h = hue / 360;
            var s = saturation / 100;
            var l = lightness / 100;

            s = l < 0.5 ? s * l + l : s * (1.0 - l) + l;
            var l2 = 2 * l - s;

            return new Rgb(
                255 * Channel(l2, s, h + 1.0 / 3),
                255 * Channel(l2, s, h),
                255 * Channel(l2, s, h - 1.0 / 3));
        }

        private static double Channel(double t1, double t2, double hue)
        {
            hue = hue < 0 ? hue + 1 : hue > 1 ? hue - 1 : hue;
            if (hue * 6 < 1) return t1 + (t2 - t1) * 6 * hue;
            if (hue * 2 < 1) return t2;
            if (hue * 3 < 2) return t1 + (t2 - t1) * (2.0 / 3 - hue) * 6;
            return t1;
        }

        private static double Clamp(double value) => value < 0 ? 0 : value > 255 ? 255 : value;

        public static byte ToByte(double value)
        {
            var clamped = Math.Round(Clamp(value));
            return (byte)clamped;
        }
    }
}
