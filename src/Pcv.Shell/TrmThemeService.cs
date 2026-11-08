using System;
using System.Windows;
using Pcv.Modular.Contracts;

namespace Pcv.Shell
{
    /// <summary>
    /// TRM 的浅色 / 深色切换。上游有 system / light / dark 三态，这里先落两态：
    /// WPF 没有「跟随系统」的现成通知，且产品要能在服务设置里明确指定。
    ///
    /// 换主题不是把窗口重建一遍，而是把令牌字典里的每个 key 重新写进
    /// Application.Resources 的直接项 —— 直接项优先于 MergedDictionaries，
    /// 每次赋值都会让 DynamicResource 重新解析，已经开着的窗口也会跟着变。
    /// 界面若用 StaticResource 就换不动，这正是校验器不许它出现的原因。
    /// </summary>
    public static class TrmThemeService
    {
        public const string DarkModeKey = "TrmDarkMode";

        private static readonly Uri LightDictionary =
            new Uri("Styles/TrmTheme.xaml", UriKind.Relative);
        private static readonly Uri DarkDictionary =
            new Uri("Styles/TrmTheme.Dark.xaml", UriKind.Relative);

        public static bool IsDark { get; private set; }

        /// <summary>换完主题通知已打开的窗口：重建用 getter 取 Brush 的卡片。</summary>
        public static event Action<bool> ThemeChanged;

        /// <summary>启动第一个窗口之前调用，把上次的选择读回来。</summary>
        public static void Initialize(ISettingsService settings)
        {
            var wanted = settings != null && settings.GetValue(DarkModeKey, false);
            Apply(wanted);
        }

        public static void Apply(bool dark, ISettingsService settings = null)
        {
            var app = Application.Current;
            if (app != null)
            {
                var source = new ResourceDictionary { Source = dark ? DarkDictionary : LightDictionary };
                var keys = new object[source.Keys.Count];
                source.Keys.CopyTo(keys, 0);
                foreach (var key in keys) app.Resources[key] = source[key];
            }
            IsDark = dark;
            ThemeChanged?.Invoke(dark);
            if (settings != null)
            {
                settings.SetValue(DarkModeKey, dark);
                settings.Save();
            }
        }
    }
}
