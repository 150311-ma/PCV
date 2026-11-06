using System.Collections.Generic;
using Pcv.Modular.Contracts;

namespace Pcv.Theme
{
    /// <summary>
    /// 主题预设：一组 HSL 参数就是一套主题，加主题只需要在这里多一行。
    /// </summary>
    public static class Presets
    {
        public static List<ThemeInfo> CreateAll() => new List<ThemeInfo>
        {
            new ThemeInfo { Id = "pcl-blue", Name = "默认蓝", Hue = 210, Saturation = 85, LightAdjust = 0, TopbarDelta = 0 },
            new ThemeInfo { Id = "sky", Name = "晴空", Hue = 200, Saturation = 80, LightAdjust = 4, TopbarDelta = 6 },
            new ThemeInfo { Id = "deep-sea", Name = "深海", Hue = 220, Saturation = 72, LightAdjust = -12, TopbarDelta = -8 },
            new ThemeInfo { Id = "violet", Name = "紫罗兰", Hue = 268, Saturation = 62, LightAdjust = 0, TopbarDelta = 8 },
            new ThemeInfo { Id = "rose", Name = "玫瑰", Hue = 340, Saturation = 68, LightAdjust = 0, TopbarDelta = 6 },
            new ThemeInfo { Id = "sakura", Name = "樱粉", Hue = 330, Saturation = 52, LightAdjust = 8, TopbarDelta = 10 },
            new ThemeInfo { Id = "amber", Name = "琥珀", Hue = 35, Saturation = 78, LightAdjust = -4, TopbarDelta = 7 },
            new ThemeInfo { Id = "lemon", Name = "柠檬", Hue = 52, Saturation = 76, LightAdjust = -8, TopbarDelta = 9 },
            new ThemeInfo { Id = "lime", Name = "青柠", Hue = 100, Saturation = 58, LightAdjust = -6, TopbarDelta = 8 },
            new ThemeInfo { Id = "mint", Name = "薄荷", Hue = 162, Saturation = 55, LightAdjust = 0, TopbarDelta = 6 },
            new ThemeInfo { Id = "graphite", Name = "水墨", Hue = 210, Saturation = 8, LightAdjust = -6, TopbarDelta = 0 },
            new ThemeInfo { Id = "jump-red", Name = "跳票红", Hue = 356, Saturation = 82, LightAdjust = -8, TopbarDelta = 5 }
        };

        public static ThemeInfo Default => CreateAll()[0];
    }
}
