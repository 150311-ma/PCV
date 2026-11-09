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
    /// 宿主开放给框架的受控能力。
    ///
    /// 为什么是接口而不是把窗口对象交出去：框架本来只能被宿主调用，
    /// 想「反过来说一句话」时如果直接拿到 WPF 窗口，就能关窗、改标题、
    /// 遍历整棵视觉树、访问宿主的私有字段 —— 权限等于宿主本身。
    /// 这里只列出宿主同意被用的几件事，逐项由宿主实现，框架拿不到别的东西。
    /// 契约程序集不引用 WPF，所以这里的 DTO 全是纯数据。
    /// </summary>
    public interface IHostControl
    {
        string HostVersion { get; }
        string BaseDirectory { get; }
        /// <summary>已安装模块的只读快照。框架可以据此列菜单、判断依赖。</summary>
        IReadOnlyList<HostModuleInfo> Modules { get; }

        /// <summary>切换主界面页面：home / install / frameworks / market / credits。成功返回 true。</summary>
        bool Navigate(string view);
        /// <summary>打开一个已安装模块（进程型会起子进程）。</summary>
        bool OpenModule(string moduleId);
        /// <summary>重新扫描模块目录并刷新界面。装完东西后调一下。</summary>
        bool RefreshModules();
        /// <summary>安装本地 .pclpkg。仍走安装器的完整校验，不绕过安全检查。</summary>
        bool InstallPackage(string packagePath, bool overwrite);
        /// <summary>卸载模块。基础模块会被宿主拒绝。</summary>
        bool UninstallModule(string moduleId);
        /// <summary>在状态栏说一句话，可选是否算错误。</summary>
        void Notify(string message, bool isError = false);
        /// <summary>写剪贴板、打开外部链接这类小事，宿主统一处理失败。</summary>
        bool CopyText(string text);
        bool OpenUrl(string url);
        /// <summary>模块列表或当前页面变了。参数是变化类型：modules / view。</summary>
        event Action<string> HostChanged;
    }

    /// <summary>模块的只读描述，刻意不含任何 UI 类型与运行中的实例。</summary>
    public sealed class HostModuleInfo
    {
        public string Id { get; set; }
        public string Name { get; set; }
        public string Version { get; set; }
        public string Category { get; set; }
        public string State { get; set; }
        public string Description { get; set; }
        public string Icon { get; set; }
        public string Accent { get; set; }
        public bool CanOpen { get; set; }
        public override string ToString() => $"{Name} {Version}";
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

    // ── 在线服务：API 网关 ────────────────────────────────────────────────
    // 「已有接口直接用，新功能通过 API 接入」的落点：壳和别的模块都只认
    // IApiGateway，不各自写一份 HTTP；接口清单来自服务端，加功能不用改壳。
    // 契约程序集不引用 WPF / Newtonsoft，所以这里全是纯数据 DTO。

    public sealed class ApiEndpointInfo
    {
        public string Id { get; set; }
        public string Name { get; set; }
        public string Method { get; set; }
        public string Path { get; set; }
        public bool NeedsKey { get; set; }
        public bool Enabled { get; set; }
        public string Note { get; set; }
        public override string ToString() => Name + "  " + Method + " " + Path;
    }

    public sealed class ApiCallResult
    {
        public bool Ok { get; set; }
        public int Status { get; set; }
        public string Message { get; set; }
        public string Body { get; set; }
    }

    public interface IApiGateway
    {
        string Site { get; }
        IReadOnlyList<ApiEndpointInfo> Endpoints { get; }
        void SetEnabled(string id, bool enabled);
        /// <summary>从服务端拉最新接口清单；失败保留本地那份并回 false。</summary>
        Task<bool> RefreshAsync();
        /// <summary>按 id 调一个已登记的接口，path 只能来自接口清单。</summary>
        Task<ApiCallResult> CallAsync(string id, string body = null);
        event Action<string> Changed;
    }

    // ── 在线服务：凭证校验 ──────────────────────────────────────────────
    //
    // 重要：**客户端不持有凭证**，也不自己比对。
    // 凭证只在博客后台（管理员会话）里显示，用户抄下来手输；
    // 对不对由服务端判，客户端只负责把用户输入交上去、拿回 ok / 不 ok。
    //
    // 为什么不是本地比对：本地比对要求客户端知道正确答案 —— 而知道答案
    // 就等于能自己解锁，「输入凭证」纯属表演。以前那版就是这么干的，
    // 而且那个「正确答案」还是从服务端用共享密钥换来的（密钥明文写在
    // 开源仓库里），等于谁都能拿到。

    public sealed class AdminKeyInfo
    {
        /// <summary>校验结果：true = 用户输入的凭证被服务端认可。</summary>
        public bool Verified { get; set; }
        public int Interval { get; set; }
        public int ExpiresIn { get; set; }
        public string ServerTime { get; set; }
        public bool Online { get; set; }
        public string Error { get; set; }
        public string ExpiresText => ExpiresIn > 0
            ? (ExpiresIn / 60) + " 分 " + (ExpiresIn % 60) + " 秒"
            : "—";
    }

    public interface IAdminKeyProvider
    {
        /// <summary>最近一次校验的结果（不包含凭证本身，客户端不该有它）。</summary>
        AdminKeyInfo Current { get; set; }

        /// <summary>把用户输入的凭证交给服务端判；离线或服务端拒绝都回 false。</summary>
        Task<AdminKeyInfo> VerifyAsync(string input);

        /// <summary>校验结果变化时通知界面。</summary>
        event Action<AdminKeyInfo> KeyChanged;
    }
}
