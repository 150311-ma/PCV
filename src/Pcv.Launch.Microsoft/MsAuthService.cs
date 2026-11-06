using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;
using Pcv.Modular.Abstractions;
using Pcv.Modular.Contracts;

namespace Pcv.Launch.Microsoft
{
    /// <summary>
    /// 微软账号登录（设备码流程）。
    ///
    /// 链路：OAuth 设备码 → Xbox Live → XSTS → Minecraft Services → 档案。
    /// 每一段都可能失败，所以每一段都单独报错，不把"登录失败"这种废话丢给用户。
    ///
    /// ClientId 不内置：开源项目带一个别人申请的 ClientId，等于把别人的配额挂在外面。
    /// </summary>
    public sealed class MsAuthService : IMicrosoftAuthService
    {
        private const string DeviceCodeEndpoint = "https://login.microsoftonline.com/consumers/oauth2/v2.0/devicecode";
        private const string TokenEndpoint = "https://login.microsoftonline.com/consumers/oauth2/v2.0/token";
        private const string XblEndpoint = "https://user.auth.xboxlive.com/user/authenticate";
        private const string XstsEndpoint = "https://xsts.auth.xboxlive.com/xsts/authorize";
        private const string McLoginEndpoint = "https://api.minecraftservices.com/authentication/login_with_xbox";
        private const string McProfileEndpoint = "https://api.minecraftservices.com/minecraft/profile";
        private const string Scope = "XboxLive.signin offline_access";

        private static readonly HttpClient Http = CreateClient();

        private readonly ILogger _log;
        private readonly ISettingsService _settings;
        private MsAccount _cached;

        public MsAuthService(ILogger log, ISettingsService settings)
        {
            _log = log;
            _settings = settings;
            _cached = Restore();
        }

        public string ClientId
        {
            get
            {
                var fromSettings = _settings?.GetValue("MsClientId", "");
                if (!string.IsNullOrWhiteSpace(fromSettings)) return fromSettings.Trim();

                var fromEnvironment = Environment.GetEnvironmentVariable("PCL_MS_CLIENT_ID");
                return string.IsNullOrWhiteSpace(fromEnvironment) ? "" : fromEnvironment.Trim();
            }
        }

        public bool IsConfigured => !string.IsNullOrWhiteSpace(ClientId);

        public MsAccount CachedAccount => _cached;

        private static HttpClient CreateClient()
        {
            var client = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
            client.DefaultRequestHeaders.UserAgent.ParseAdd("PCV-MsAuth/1.0");
            return client;
        }

        public async Task<DeviceCodeInfo> RequestDeviceCodeAsync(CancellationToken cancellationToken)
        {
            EnsureConfigured();

            var body = await PostFormAsync(DeviceCodeEndpoint, new Dictionary<string, string>
            {
                ["client_id"] = ClientId,
                ["scope"] = Scope
            }, cancellationToken).ConfigureAwait(false);

            var device = JObject.Parse(body);
            var info = new DeviceCodeInfo
            {
                DeviceCode = (string)device["device_code"],
                UserCode = (string)device["user_code"],
                VerificationUri = (string)device["verification_uri"],
                ExpiresInSeconds = (int?)device["expires_in"] ?? 900,
                IntervalSeconds = (int?)device["interval"] ?? 5,
                Message = (string)device["message"]
            };

            _log.Info($"已申请设备码，去 {info.VerificationUri} 输入 {info.UserCode}");
            return info;
        }

        public async Task<MsAccount> CompleteLoginAsync(DeviceCodeInfo deviceCode, IProgress<string> progress,
            CancellationToken cancellationToken)
        {
            EnsureConfigured();
            if (deviceCode == null) throw new ArgumentNullException(nameof(deviceCode));

            var deadline = DateTime.UtcNow.AddSeconds(deviceCode.ExpiresInSeconds);
            var interval = Math.Max(1, deviceCode.IntervalSeconds);
            JObject tokens = null;

            while (DateTime.UtcNow < deadline)
            {
                await Task.Delay(TimeSpan.FromSeconds(interval), cancellationToken).ConfigureAwait(false);

                string body;
                try
                {
                    body = await PostFormAsync(TokenEndpoint, new Dictionary<string, string>
                    {
                        ["grant_type"] = "urn:ietf:params:oauth:grant-type:device_code",
                        ["device_code"] = deviceCode.DeviceCode,
                        ["client_id"] = ClientId
                    }, cancellationToken).ConfigureAwait(false);
                }
                catch (HttpRequestException ex) when (TryGetError(ex, out var error))
                {
                    // authorization_pending 是正常等待，不是失败
                    switch (error)
                    {
                        case "authorization_pending":
                            progress?.Report("还在等你授权…");
                            continue;
                        case "slow_down":
                            interval += 5;
                            progress?.Report($"服务要求放慢轮询，间隔改为 {interval} 秒");
                            continue;
                        case "expired_token":
                            throw new InvalidOperationException("设备码过期了，重新来一次");
                        case "authorization_declined":
                            throw new InvalidOperationException("你拒绝了授权");
                        default:
                            throw new InvalidOperationException("登录失败：" + error);
                    }
                }

                tokens = JObject.Parse(body);
                break;
            }

            if (tokens == null) throw new TimeoutException("等超时了，没有拿到令牌");

            var refreshToken = (string)tokens["refresh_token"] ?? "";
            var accessToken = (string)tokens["access_token"];
            return await CompleteFromOAuthAsync(accessToken, refreshToken, progress, cancellationToken)
                .ConfigureAwait(false);
        }

        public async Task<MsAccount> RefreshAsync(string refreshToken, CancellationToken cancellationToken)
        {
            EnsureConfigured();
            if (string.IsNullOrWhiteSpace(refreshToken))
                throw new ArgumentException("没有 refresh token", nameof(refreshToken));

            var body = await PostFormAsync(TokenEndpoint, new Dictionary<string, string>
            {
                ["grant_type"] = "refresh_token",
                ["refresh_token"] = refreshToken,
                ["client_id"] = ClientId,
                ["scope"] = Scope
            }, cancellationToken).ConfigureAwait(false);

            var tokens = JObject.Parse(body);
            return await CompleteFromOAuthAsync(
                (string)tokens["access_token"],
                (string)tokens["refresh_token"] ?? refreshToken,
                null,
                cancellationToken).ConfigureAwait(false);
        }

        /// <summary>OAuth 拿到之后的三段：XBL → XSTS → Minecraft 档案。</summary>
        private async Task<MsAccount> CompleteFromOAuthAsync(string oauthAccessToken, string refreshToken,
            IProgress<string> progress, CancellationToken cancellationToken)
        {
            progress?.Report("正在换 Xbox Live 令牌…");
            var xblBody = await PostJsonAsync(XblEndpoint, new
            {
                Properties = new
                {
                    AuthMethod = "RPS",
                    SiteName = "user.auth.xboxlive.com",
                    RpsTicket = "d=" + oauthAccessToken
                },
                RelyingParty = "http://auth.xboxlive.com",
                TokenType = "JWT"
            }, cancellationToken).ConfigureAwait(false);

            var xblToken = (string)JObject.Parse(xblBody)["Token"];

            progress?.Report("正在换 XSTS 授权…");
            var xstsBody = await PostJsonAsync(XstsEndpoint, new
            {
                Properties = new { SandboxId = "RETAIL", UserTokens = new[] { xblToken } },
                RelyingParty = "rp://api.minecraftservices.com/",
                TokenType = "JWT"
            }, cancellationToken).ConfigureAwait(false);

            var xsts = JObject.Parse(xstsBody);
            var userHash = (string)xsts["DisplayClaims"]?["xui"]?[0]?["uhs"];

            progress?.Report("正在换 Minecraft 令牌…");
            var mcBody = await PostJsonAsync(McLoginEndpoint, new
            {
                identityToken = $"XBL3.0 x={userHash};{xsts["Token"]}"
            }, cancellationToken).ConfigureAwait(false);

            var mc = JObject.Parse(mcBody);
            var mcToken = (string)mc["access_token"];
            var expiresIn = (int?)mc["expires_in"] ?? 86400;

            progress?.Report("正在取账号档案…");
            using (var request = new HttpRequestMessage(HttpMethod.Get, McProfileEndpoint))
            {
                request.Headers.TryAddWithoutValidation("Authorization", "Bearer " + mcToken);
                using (var response = await Http.SendAsync(request, cancellationToken).ConfigureAwait(false))
                {
                    var profileJson = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
                    if (!response.IsSuccessStatusCode)
                    {
                        if ((int)response.StatusCode == 404)
                            throw new InvalidOperationException("这个账号没有 Minecraft: Java 版（没买，或者档案还没建）");
                        throw new InvalidOperationException($"取档案失败（HTTP {(int)response.StatusCode}）：{profileJson}");
                    }

                    var profile = JObject.Parse(profileJson);
                    var account = new MsAccount
                    {
                        Name = (string)profile["name"],
                        Uuid = (string)profile["id"],
                        AccessToken = mcToken,
                        RefreshToken = refreshToken,
                        ExpiresAtUnix = DateTimeOffset.UtcNow.AddSeconds(expiresIn).ToUnixTimeSeconds(),
                        ProfileJson = profileJson
                    };

                    Persist(account);
                    _log.Info($"微软登录成功：{account.Name}");
                    progress?.Report("登录成功：" + account.Name);
                    return account;
                }
            }
        }

        private void Persist(MsAccount account)
        {
            if (_settings == null || account == null) return;
            _settings.SetValue("MsAccessToken", account.AccessToken);
            _settings.SetValue("MsRefreshToken", account.RefreshToken);
            _settings.SetValue("MsName", account.Name);
            _settings.SetValue("MsUuid", account.Uuid);
            _settings.SetValue("MsExpiresAt", account.ExpiresAtUnix);
            _settings.SetValue("MsProfileJson", account.ProfileJson);
            _settings.Save();
            _cached = account;
        }

        private MsAccount Restore()
        {
            if (_settings == null) return null;
            var name = _settings.GetValue("MsName", "");
            var token = _settings.GetValue("MsAccessToken", "");
            if (string.IsNullOrWhiteSpace(name) || string.IsNullOrWhiteSpace(token)) return null;

            return new MsAccount
            {
                Name = name,
                Uuid = _settings.GetValue("MsUuid", ""),
                AccessToken = token,
                RefreshToken = _settings.GetValue("MsRefreshToken", ""),
                ExpiresAtUnix = _settings.GetValue("MsExpiresAt", 0L),
                ProfileJson = _settings.GetValue("MsProfileJson", "")
            };
        }

        private void EnsureConfigured()
        {
            if (IsConfigured) return;
            throw new InvalidOperationException(
                "没有配置 ClientId。开源版不带内置的——请设置环境变量 PCL_MS_CLIENT_ID，" +
                "或在配置里写 MsClientId。");
        }

        // ── HTTP ────────────────────────────────────────────────────────────

        private async Task<string> PostFormAsync(string url, Dictionary<string, string> form,
            CancellationToken cancellationToken)
        {
            using (var content = new FormUrlEncodedContent(form))
            using (var response = await Http.PostAsync(url, content, cancellationToken).ConfigureAwait(false))
            {
                var body = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
                if (!response.IsSuccessStatusCode)
                    throw new HttpRequestException($"HTTP {(int)response.StatusCode}: {body}");
                return body;
            }
        }

        private async Task<string> PostJsonAsync(string url, object payload, CancellationToken cancellationToken)
        {
            var json = Newtonsoft.Json.JsonConvert.SerializeObject(payload);
            using (var content = new StringContent(json, Encoding.UTF8, "application/json"))
            using (var response = await Http.PostAsync(url, content, cancellationToken).ConfigureAwait(false))
            {
                var body = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
                if (!response.IsSuccessStatusCode)
                {
                    if (url.StartsWith(XstsEndpoint, StringComparison.OrdinalIgnoreCase))
                        throw new InvalidOperationException(DescribeXstsError(body));
                    throw new InvalidOperationException($"HTTP {(int)response.StatusCode}（{url}）：{body}");
                }
                return body;
            }
        }

        /// <summary>从 OAuth 的错误响应里抠出 error 字段（authorization_pending 之类）。</summary>
        private static bool TryGetError(HttpRequestException exception, out string error)
        {
            error = null;
            var message = exception?.Message ?? "";
            var index = message.IndexOf('{');
            if (index < 0) return false;

            try
            {
                error = (string)JObject.Parse(message.Substring(index))["error"];
                return !string.IsNullOrEmpty(error);
            }
            catch
            {
                return false;
            }
        }

        /// <summary>XSTS 的 XErr 是数字码，直接抛给用户等于没说。翻译成人话。</summary>
        private static string DescribeXstsError(string body)
        {
            string xerr;
            try
            {
                xerr = (string)JObject.Parse(body)["XErr"];
            }
            catch
            {
                return "XSTS 授权失败：" + body;
            }

            switch (xerr)
            {
                case "2148916233": return "该微软账号还没建 Xbox 档案，先去 xbox.com 登录一次";
                case "2148916235": return "该账号所在地区不支持 Xbox Live";
                case "2148916236":
                case "2148916237": return "该账号需要完成成年人验证（可能受地区限制）";
                case "2148916238": return "该账号是未成年人账号，需要先加入家庭组";
                default: return $"XSTS 授权失败（XErr={xerr}）：{body}";
            }
        }
    }
}
