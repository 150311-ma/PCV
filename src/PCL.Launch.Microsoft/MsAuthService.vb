Imports System.Collections.Generic
Imports System.Net
Imports System.Net.Http
Imports System.Text
Imports System.Threading
Imports System.Threading.Tasks
Imports Newtonsoft.Json.Linq
Imports Pcv.Modular.Abstractions
Imports Pcv.Modular.Contracts

Namespace Global.PCL.Launch.Microsoft

    ''' <summary>
    ''' 微软账号登录（设备码流程）。
    '''
    ''' 迁移自上游 ModLaunch.vb 的 McLoginMsStart / MsLoginStep1~6，链路完全一致：
    '''   OAuth 设备码 → XBL 认证 → XSTS 授权 → Minecraft Services 换取 AccessToken → 拉取档案
    ''' 区别只在于：这里不碰 PCL 的全局 Settings，也不弹窗，进度通过 IProgress 抛给调用方。
    ''' </summary>
    Public Class MsAuthService
        Implements IMicrosoftAuthService

        Private Const DeviceCodeEndpoint As String = "https://login.microsoftonline.com/consumers/oauth2/v2.0/devicecode"
        Private Const TokenEndpoint As String = "https://login.microsoftonline.com/consumers/oauth2/v2.0/token"
        Private Const XblEndpoint As String = "https://user.auth.xboxlive.com/user/authenticate"
        Private Const XstsEndpoint As String = "https://xsts.auth.xboxlive.com/xsts/authorize"
        Private Const McLoginEndpoint As String = "https://api.minecraftservices.com/authentication/login_with_xbox"
        Private Const McProfileEndpoint As String = "https://api.minecraftservices.com/minecraft/profile"
        Private Const Scope As String = "XboxLive.signin offline_access"

        Private ReadOnly _http As HttpClient
        Private ReadOnly _log As ILogger
        Private ReadOnly _settings As ISettingsService

        Public Sub New(log As ILogger, settings As ISettingsService)
            _log = log
            _settings = settings
            ServicePointManager.SecurityProtocol = ServicePointManager.SecurityProtocol Or SecurityProtocolType.Tls12
            _http = New HttpClient()
            _http.Timeout = TimeSpan.FromSeconds(60)
            _http.DefaultRequestHeaders.Add("User-Agent", "PCL-Modular/1.0")
        End Sub

        ''' <summary>上游开源版里 ClientId 同样留空，需要使用者自己申请（环境变量 PCL_MS_CLIENT_ID）。</summary>
        Public ReadOnly Property ClientId As String Implements IMicrosoftAuthService.ClientId
            Get
                Dim fromEnvironment = Environment.GetEnvironmentVariable("PCL_MS_CLIENT_ID")
                If Not String.IsNullOrWhiteSpace(fromEnvironment) Then Return fromEnvironment.Trim()
                If _settings IsNot Nothing Then Return _settings.GetValue(Of String)("MsClientId", "")
                Return ""
            End Get
        End Property

        Public ReadOnly Property IsConfigured As Boolean Implements IMicrosoftAuthService.IsConfigured
            Get
                Return Not String.IsNullOrWhiteSpace(ClientId)
            End Get
        End Property

        Public ReadOnly Property CachedAccount As MsAccount Implements IMicrosoftAuthService.CachedAccount
            Get
                If _settings Is Nothing Then Return Nothing
                Dim token = _settings.GetValue(Of String)("MsAccessToken", "")
                If String.IsNullOrWhiteSpace(token) Then Return Nothing
                Return New MsAccount With {
                    .Name = _settings.GetValue(Of String)("MsName", ""),
                    .Uuid = _settings.GetValue(Of String)("MsUuid", ""),
                    .AccessToken = token,
                    .RefreshToken = _settings.GetValue(Of String)("MsRefreshToken", ""),
                    .ExpiresAtUnix = _settings.GetValue(Of Long)("MsExpiresAt", 0),
                    .ProfileJson = _settings.GetValue(Of String)("MsProfileJson", "")}
            End Get
        End Property

        ' ── OAuth 设备码 ─────────────────────────────────────────────────────

        Public Async Function RequestDeviceCodeAsync(cancellationToken As CancellationToken) As Task(Of DeviceCodeInfo) Implements IMicrosoftAuthService.RequestDeviceCodeAsync
            EnsureConfigured()

            Dim body = New Dictionary(Of String, String) From {
                {"client_id", ClientId},
                {"scope", Scope}
            }
            Dim text = Await PostFormAsync(DeviceCodeEndpoint, body, cancellationToken).ConfigureAwait(False)
            Dim json = JObject.Parse(text)

            Dim info = New DeviceCodeInfo With {
                .DeviceCode = json.Value(Of String)("device_code"),
                .UserCode = json.Value(Of String)("user_code"),
                .VerificationUri = json.Value(Of String)("verification_uri"),
                .ExpiresInSeconds = json.Value(Of Integer)("expires_in"),
                .IntervalSeconds = If(json("interval") Is Nothing, 5, json.Value(Of Integer)("interval")),
                .Message = json.Value(Of String)("message")}

            _log.Info($"已获取设备码：{info.UserCode}（{info.ExpiresInSeconds} 秒内有效）")
            Return info
        End Function

        Public Async Function CompleteLoginAsync(deviceCode As DeviceCodeInfo, progress As IProgress(Of String), cancellationToken As CancellationToken) As Task(Of MsAccount) Implements IMicrosoftAuthService.CompleteLoginAsync
            If deviceCode Is Nothing Then Throw New ArgumentNullException(NameOf(deviceCode))
            EnsureConfigured()

            progress?.Report($"请在浏览器打开 {deviceCode.VerificationUri}，输入代码 {deviceCode.UserCode}")

            Dim interval = Math.Max(1, deviceCode.IntervalSeconds)
            Dim deadline = DateTime.UtcNow.AddSeconds(Math.Max(30, deviceCode.ExpiresInSeconds))
            Dim accessToken As String = Nothing
            Dim refreshToken As String = Nothing

            While True
                If DateTime.UtcNow > deadline Then Throw New TimeoutException("设备码已过期，请重新获取")
                cancellationToken.ThrowIfCancellationRequested()
                Await Task.Delay(interval * 1000, cancellationToken).ConfigureAwait(False)

                Dim body = New Dictionary(Of String, String) From {
                    {"grant_type", "urn:ietf:params:oauth:grant-type:device_code"},
                    {"device_code", deviceCode.DeviceCode},
                    {"client_id", ClientId}
                }

                Dim response As String = Nothing
                Dim failure As String = Nothing
                Dim failureCode As String = Nothing
                Dim pair = Await PostFormAllowErrorAsync(TokenEndpoint, body, cancellationToken).ConfigureAwait(False)
                response = pair.Item1
                failure = pair.Item2
                failureCode = pair.Item3

                If failure Is Nothing Then
                    Dim json = JObject.Parse(response)
                    accessToken = json.Value(Of String)("access_token")
                    refreshToken = json.Value(Of String)("refresh_token")
                    Exit While
                End If

                Select Case failureCode
                    Case "authorization_pending"
                        progress?.Report("等待在浏览器中完成授权…")
                    Case "slow_down"
                        interval += 5
                        progress?.Report("授权服务要求降低轮询频率，已调整间隔")
                    Case "expired_token"
                        Throw New TimeoutException("设备码已过期，请重新获取")
                    Case "authorization_declined"
                        Throw New OperationCanceledException("用户拒绝了授权")
                    Case "bad_verification_code"
                        Throw New InvalidOperationException("设备码无效，请重新获取")
                    Case Else
                        Throw New InvalidOperationException($"登录失败（{failureCode}）：{failure}")
                End Select
            End While

            progress?.Report("OAuth 通过，正在完成 Xbox Live 认证…")
            Dim account = Await ExchangeToMinecraftAsync(accessToken, refreshToken, progress, cancellationToken).ConfigureAwait(False)
            Persist(account)
            Return account
        End Function

        Public Async Function RefreshAsync(refreshToken As String, cancellationToken As CancellationToken) As Task(Of MsAccount) Implements IMicrosoftAuthService.RefreshAsync
            EnsureConfigured()
            If String.IsNullOrWhiteSpace(refreshToken) Then Throw New ArgumentException("缺少 RefreshToken", NameOf(refreshToken))

            Dim body = New Dictionary(Of String, String) From {
                {"grant_type", "refresh_token"},
                {"refresh_token", refreshToken},
                {"client_id", ClientId},
                {"scope", Scope}
            }
            Dim text = Await PostFormAsync(TokenEndpoint, body, cancellationToken).ConfigureAwait(False)
            Dim json = JObject.Parse(text)
            Dim access = json.Value(Of String)("access_token")
            Dim refresh = If(json("refresh_token") Is Nothing, refreshToken, json.Value(Of String)("refresh_token"))
            Dim account = Await ExchangeToMinecraftAsync(access, refresh, Nothing, cancellationToken).ConfigureAwait(False)
            Persist(account)
            Return account
        End Function

        ' ── Xbox Live 链路 ──────────────────────────────────────────────────

        Private Async Function ExchangeToMinecraftAsync(oauthAccessToken As String, refreshToken As String,
                                                        progress As IProgress(Of String), cancellationToken As CancellationToken) As Task(Of MsAccount)

            ' Step 2：XBL 认证，RpsTicket 要带 "d=" 前缀
            Dim xblPayload = New JObject From {
                {"Properties", New JObject From {
                    {"AuthMethod", "RPS"},
                    {"SiteName", "user.auth.xboxlive.com"},
                    {"RpsTicket", "d=" & oauthAccessToken}}},
                {"RelyingParty", "http://auth.xboxlive.com"},
                {"TokenType", "JWT"}
            }
            Dim xbl = JObject.Parse(Await PostJsonAsync(XblEndpoint, xblPayload, cancellationToken).ConfigureAwait(False))
            Dim xblToken = xbl.Value(Of String)("Token")
            Dim userHash = xbl("DisplayClaims")?("xui")?(0)?("uhs")?.ToString()
            If String.IsNullOrEmpty(xblToken) OrElse String.IsNullOrEmpty(userHash) Then
                Throw New InvalidOperationException("Xbox Live 认证未返回预期的令牌")
            End If
            progress?.Report("Xbox Live 认证通过，正在获取 XSTS 授权…")

            ' Step 3：XSTS 授权，失败时要按 XErr 给出可读原因
            Dim xstsPayload = New JObject From {
                {"Properties", New JObject From {
                    {"SandboxId", "RETAIL"},
                    {"UserTokens", New JArray From {xblToken}}}},
                {"RelyingParty", "rp://api.minecraftservices.com/"},
                {"TokenType", "JWT"}
            }
            Dim xstsResponse = Await SendJsonAsync(XstsEndpoint, xstsPayload, cancellationToken).ConfigureAwait(False)
            If Not xstsResponse.Item1 Then
                Throw New InvalidOperationException(DescribeXstsError(xstsResponse.Item2))
            End If
            Dim xsts = JObject.Parse(xstsResponse.Item2)
            Dim xstsToken = xsts.Value(Of String)("Token")
            If String.IsNullOrEmpty(xstsToken) Then Throw New InvalidOperationException("XSTS 授权未返回令牌")
            progress?.Report("XSTS 授权通过，正在换取 Minecraft 访问令牌…")

            ' Step 4：用 XSTS 令牌换 Minecraft AccessToken
            Dim mcPayload = New JObject From {
                {"identityToken", $"XBL3.0 x={userHash};{xstsToken}"}
            }
            Dim mc = JObject.Parse(Await PostJsonAsync(McLoginEndpoint, mcPayload, cancellationToken).ConfigureAwait(False))
            Dim mcToken = mc.Value(Of String)("access_token")
            Dim expiresIn = If(mc("expires_in") Is Nothing, 86400, mc.Value(Of Long)("expires_in"))
            If String.IsNullOrEmpty(mcToken) Then Throw New InvalidOperationException("Minecraft Services 未返回访问令牌")
            progress?.Report("已获得 Minecraft 访问令牌，正在读取玩家档案…")

            ' Step 6：档案（可能为空，表示该账号没有购买游戏）
            Dim profileJson = Await GetProfileAsync(mcToken, cancellationToken).ConfigureAwait(False)
            Dim profile = If(String.IsNullOrEmpty(profileJson), Nothing, JObject.Parse(profileJson))
            If profile Is Nothing OrElse profile("name") Is Nothing Then
                Throw New InvalidOperationException("该账号没有可用的 Minecraft: Java 版档案（可能未购买游戏）")
            End If

            progress?.Report($"登录成功：{profile.Value(Of String)("name")}")
            _log.Info($"微软登录成功：{profile.Value(Of String)("name")}")

            Return New MsAccount With {
                .Name = profile.Value(Of String)("name"),
                .Uuid = profile.Value(Of String)("id"),
                .AccessToken = mcToken,
                .RefreshToken = refreshToken,
                .ExpiresAtUnix = DateTimeOffset.UtcNow.AddSeconds(expiresIn).ToUnixTimeSeconds(),
                .ProfileJson = profileJson}
        End Function

        Private Shared Function DescribeXstsError(body As String) As String
            Dim xerr As String = Nothing
            Try
                xerr = JObject.Parse(body)("XErr")?.ToString()
            Catch
                Return $"XSTS 授权失败：{body}"
            End Try

            Select Case xerr
                Case "2148916233"
                    Return "该微软账号尚未创建 Xbox 档案，请先在 xbox.com 登录一次"
                Case "2148916235"
                    Return "该账号所在地区不支持 Xbox Live"
                Case "2148916236", "2148916237"
                    Return "该账号需要完成成年人验证（可能受地区限制）"
                Case "2148916238"
                    Return "该账号是未成年人账号，需要先加入家庭组"
                Case Else
                    Return $"XSTS 授权失败（XErr={xerr}）：{body}"
            End Select
        End Function

        ' ── HTTP 细节 ───────────────────────────────────────────────────────

        Private Sub EnsureConfigured()
            If IsConfigured Then Return
            Throw New InvalidOperationException(
                "未配置微软登录 ClientId。开源版 PCL 不含内置 ClientId，" &
                "请设置环境变量 PCL_MS_CLIENT_ID，或在设置中填写 MsClientId。")
        End Sub

        Private Async Function PostFormAsync(url As String, body As IDictionary(Of String, String), cancellationToken As CancellationToken) As Task(Of String)
            Dim pair = Await PostFormAllowErrorAsync(url, body, cancellationToken).ConfigureAwait(False)
            If pair.Item2 IsNot Nothing Then Throw New InvalidOperationException($"请求失败（{pair.Item3}）：{pair.Item2}")
            Return pair.Item1
        End Function

        ''' <summary>返回 (响应体, 错误描述, 错误码)；成功时错误为 Nothing。</summary>
        Private Async Function PostFormAllowErrorAsync(url As String, body As IDictionary(Of String, String), cancellationToken As CancellationToken) As Task(Of Tuple(Of String, String, String))
            Using content = New FormUrlEncodedContent(body)
                Using response = Await _http.PostAsync(url, content, cancellationToken).ConfigureAwait(False)
                    Dim text = Await response.Content.ReadAsStringAsync().ConfigureAwait(False)
                    If response.IsSuccessStatusCode Then Return Tuple.Create(text, CType(Nothing, String), CType(Nothing, String))

                    Dim code As String = Nothing
                    Dim description As String = text
                    Try
                        Dim json = JObject.Parse(text)
                        code = json.Value(Of String)("error")
                        description = If(json.Value(Of String)("error_description"), text)
                        ' 设备码流程的错误码在 error 字段里，OAuth 标准错误在 error 里，此处统一处理
                    Catch
                        ' 非 JSON 响应，原样带回
                    End Try
                    Return Tuple.Create(text, description, code)
                End Using
            End Using
        End Function

        Private Async Function PostJsonAsync(url As String, payload As JObject, cancellationToken As CancellationToken) As Task(Of String)
            Dim result = Await SendJsonAsync(url, payload, cancellationToken).ConfigureAwait(False)
            If Not result.Item1 Then Throw New InvalidOperationException($"请求 {url} 失败：{result.Item2}")
            Return result.Item2
        End Function

        Private Async Function SendJsonAsync(url As String, payload As JObject, cancellationToken As CancellationToken) As Task(Of Tuple(Of Boolean, String))
            Using content = New StringContent(payload.ToString(Newtonsoft.Json.Formatting.None), Encoding.UTF8, "application/json")
                Using response = Await _http.PostAsync(url, content, cancellationToken).ConfigureAwait(False)
                    Dim text = Await response.Content.ReadAsStringAsync().ConfigureAwait(False)
                    Return Tuple.Create(response.IsSuccessStatusCode, text)
                End Using
            End Using
        End Function

        Private Async Function GetProfileAsync(accessToken As String, cancellationToken As CancellationToken) As Task(Of String)
            Using request = New HttpRequestMessage(HttpMethod.Get, McProfileEndpoint)
                request.Headers.Authorization = New System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", accessToken)
                Using response = Await _http.SendAsync(request, cancellationToken).ConfigureAwait(False)
                    If response.StatusCode = HttpStatusCode.NotFound Then Return Nothing
                    If Not response.IsSuccessStatusCode Then
                        Throw New InvalidOperationException($"读取玩家档案失败（{CInt(response.StatusCode)}）")
                    End If
                    Return Await response.Content.ReadAsStringAsync().ConfigureAwait(False)
                End Using
            End Using
        End Function

        Private Sub Persist(account As MsAccount)
            If _settings Is Nothing OrElse account Is Nothing Then Return
            _settings.SetValue(Of String)("MsAccessToken", account.AccessToken)
            _settings.SetValue(Of String)("MsRefreshToken", account.RefreshToken)
            _settings.SetValue(Of String)("MsName", account.Name)
            _settings.SetValue(Of String)("MsUuid", account.Uuid)
            _settings.SetValue(Of Long)("MsExpiresAt", account.ExpiresAtUnix)
            _settings.SetValue(Of String)("MsProfileJson", account.ProfileJson)
            _settings.Save()
        End Sub

    End Class

End Namespace
