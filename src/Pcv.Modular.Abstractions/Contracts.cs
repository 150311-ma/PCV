using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace Pcv.Modular.Contracts
{
    /// <summary>
    /// 框架如果想在界面上"被打开"，就实现这个接口。
    /// 返回值按 WPF 的 FrameworkElement 使用——契约程序集不引用 WPF，所以这里只能是 object，
    /// 这是故意的：非 UI 的框架（比如只做后台清理的）不需要为此拖一个 WPF 依赖进来。
    /// </summary>
    public interface IFrameworkSurface
    {
        /// <summary>窗口标题。</summary>
        string Title { get; }

        /// <summary>创建界面。宿主会把它塞进窗口里。</summary>
        object CreateView(Abstractions.IModuleContext context);
    }

    /// <summary>
    /// 跨模块契约：放在 Abstractions 里，模块之间只依赖接口，不互相引用程序集。
    /// 这样 UI 模块可以用到主题/登录能力，而不需要编译期知道谁实现了它。
    /// </summary>
    public interface ISettingsService
    {
        T GetValue<T>(string key, T defaultValue = default);
        void SetValue<T>(string key, T value);
        bool Remove(string key);
        /// <summary>立即落盘。返回是否成功，失败时 LastError 里是原因。</summary>
        bool Save();
        string LastError { get; }
        IReadOnlyCollection<string> Keys { get; }
    }

    /// <summary>主题描述，不含任何 WPF 类型，保证非 UI 模块也能读。</summary>
    public sealed class ThemeInfo
    {
        public string Id { get; set; }
        public string Name { get; set; }
        /// <summary>色相 0-360。</summary>
        public double Hue { get; set; }
        /// <summary>饱和度 0-100。</summary>
        public double Saturation { get; set; }
        /// <summary>亮度偏移 -20~20。</summary>
        public double LightAdjust { get; set; }
        /// <summary>顶栏色相偏移，0 表示纯色渐变。</summary>
        public double TopbarDelta { get; set; }
        public bool IsDark { get; set; }
        public override string ToString() => Name;
    }

    public interface IThemeService
    {
        IReadOnlyList<ThemeInfo> Themes { get; }
        ThemeInfo Current { get; }
        /// <summary>应用主题并写入 Application.Current.Resources。返回是否成功。</summary>
        bool Apply(string themeId);
        /// <summary>取某个颜色令牌的 #RRGGBBAA 文本，UI 模块可用于自绘。</summary>
        string GetColor(string token);
        /// <summary>顶栏渐变的三段色标（十六进制），由主题模块算，宿主负责画。</summary>
        IReadOnlyList<ThemeGradientStop> GetTopbarGradient();
        event Action<ThemeInfo> ThemeChanged;
    }

    /// <summary>一段渐变色标。刻意不用 WPF 类型，保证契约程序集不绑死 UI 框架。</summary>
    public sealed class ThemeGradientStop
    {
        public string Color { get; set; }
        public double Offset { get; set; }
    }

    public sealed class DeviceCodeInfo
    {
        public string DeviceCode { get; set; }
        public string UserCode { get; set; }
        public string VerificationUri { get; set; }
        public int ExpiresInSeconds { get; set; }
        public int IntervalSeconds { get; set; } = 5;
        public string Message { get; set; }
    }

    public sealed class MsAccount
    {
        public string Name { get; set; }
        public string Uuid { get; set; }
        public string AccessToken { get; set; }
        public string RefreshToken { get; set; }
        public long ExpiresAtUnix { get; set; }
        public string ProfileJson { get; set; }
        public DateTimeOffset ExpiresAt => DateTimeOffset.FromUnixTimeSeconds(ExpiresAtUnix);
    }

    /// <summary>
    /// 微软账号登录。注意：开源版本的 ClientId 为空，调用方需要自行配置。
    /// </summary>
    public interface IMicrosoftAuthService
    {
        bool IsConfigured { get; }
        string ClientId { get; }
        MsAccount CachedAccount { get; }
        Task<DeviceCodeInfo> RequestDeviceCodeAsync(CancellationToken cancellationToken);
        Task<MsAccount> CompleteLoginAsync(DeviceCodeInfo deviceCode, IProgress<string> progress, CancellationToken cancellationToken);
        Task<MsAccount> RefreshAsync(string refreshToken, CancellationToken cancellationToken);
    }
}
