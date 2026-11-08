using System.Windows;
using System.Windows.Media;

namespace Pcv.Shell
{
    /// <summary>
    /// C# 侧取 TRM 设计令牌的唯一入口。
    ///
    /// XAML 用 {DynamicResource TrmXxx}，代码里用 Trm.Xxx —— 两边都指向
    /// Styles/TrmTheme*.xaml 同一份定义。写死颜色在换主题时必然是错的，
    /// 而且只有换主题那一刻才看得出来（上游 TRM UI 三条硬规矩的第一条）。
    /// tools/check_trm_theme.py 会闸住代码里重新出现的裸色值。
    /// </summary>
    internal static class Trm
    {
        public static Brush Get(string key)
        {
            var app = Application.Current;
            if (app == null) return Brushes.Transparent;
            var value = app.Resources[key];
            return value as Brush ?? Brushes.Transparent;
        }

        public static Color ColorOf(string key)
        {
            var solid = Get(key) as SolidColorBrush;
            return solid != null ? solid.Color : Colors.Transparent;
        }

        public static Brush Ink => Get("TrmInk");
        public static Brush InkMuted => Get("TrmInkMuted");
        public static Brush Help => Get("TrmHelp");
        public static Brush Meta => Get("TrmMeta");

        public static Brush Bg => Get("TrmBg");
        public static Brush Surface => Get("TrmSurface");
        public static Brush Shell => Get("TrmShell");
        public static Brush Workspace => Get("TrmWorkspace");
        public static Brush Group => Get("TrmGroup");
        public static Brush Stage => Get("TrmStage");
        public static Brush Rail => Get("TrmRail");

        public static Brush Hairline => Get("TrmHairline");
        public static Brush Line => Get("TrmLine");
        public static Brush FocusLine => Get("TrmFocusLine");

        public static Brush Accent => Get("TrmAccent");
        public static Brush AccentHover => Get("TrmAccentHover");
        public static Brush AccentInk => Get("TrmAccentInk");
        public static Brush AccentSoft => Get("TrmAccentSoft");
        public static Brush AccentSoftHover => Get("TrmAccentSoftHover");
        public static Brush AccentChipInk => Get("TrmAccentChipInk");

        public static Brush Chip => Get("TrmChip");
        public static Brush ChipInk => Get("TrmChipInk");
        public static Brush IconTile => Get("TrmIconTile");
        public static Brush White => Get("TrmWhite");
        public static Brush Transparent => Get("TrmTransparent");

        public static Brush Ok => Get("TrmOk");
        public static Brush OkSoft => Get("TrmOkSoft");
        public static Brush OkInk => Get("TrmOkInk");
        public static Brush Warn => Get("TrmWarn");
        public static Brush WarnSoft => Get("TrmWarnSoft");
        public static Brush WarnInk => Get("TrmWarnInk");
        public static Brush Danger => Get("TrmDanger");
        public static Brush DangerSoft => Get("TrmDangerSoft");
        public static Brush DangerInk => Get("TrmDangerInk");

        /// <summary>模块没声明 accent 时轮流用的六档预览色，同样来自令牌。</summary>
        public static Color[] ArtColors => new[]
        {
            ColorOf("TrmArt1"), ColorOf("TrmArt2"), ColorOf("TrmArt3"),
            ColorOf("TrmArt4"), ColorOf("TrmArt5"), ColorOf("TrmArt6"),
        };
    }
}
