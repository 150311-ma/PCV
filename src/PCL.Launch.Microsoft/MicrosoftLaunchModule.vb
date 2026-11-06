Imports Pcv.Modular.Abstractions
Imports Pcv.Modular.Contracts

Namespace Global.PCL.Launch.Microsoft

    ''' <summary>
    ''' 微软启动模块入口。
    ''' 只负责把 IMicrosoftAuthService 挂到服务表上；启动游戏属于后续的 Launch 模块，
    ''' 这里刻意不做“登录完顺便启动”，避免又把模块边界糊在一起。
    ''' </summary>
    Public Partial Class MicrosoftLaunchModule
        Implements IPclModule, IFrameworkSurface

        Private _log As ILogger
        Private _service As MsAuthService

        Public Sub OnLoad(context As IModuleContext) Implements IPclModule.OnLoad
            _log = context.Log

            Dim settings As ISettingsService = Nothing
            context.Services.TryGet(Of ISettingsService)(settings)

            _service = New MsAuthService(_log, settings)
            context.Services.Register(Of IMicrosoftAuthService)(_service)
            context.Data("msauth") = _service

            If _service.IsConfigured Then
                _log.Info("微软登录模块已装载（设备码流程，ClientId 已配置）")
            Else
                _log.Warn("微软登录模块已装载，但未配置 ClientId：设置环境变量 PCL_MS_CLIENT_ID 后才能真正登录")
            End If
        End Sub

        Public Sub OnStart() Implements IPclModule.OnStart
            If _service Is Nothing Then Return
            Dim cached = _service.CachedAccount
            If cached Is Nothing Then
                _log.Info("当前没有已登录的微软账号")
            ElseIf cached.ExpiresAt > DateTimeOffset.UtcNow Then
                _log.Info($"已登录账号 {cached.Name}，令牌有效期至 {cached.ExpiresAt.LocalDateTime:yyyy-MM-dd HH:mm}")
            Else
                _log.Info($"账号 {cached.Name} 的令牌已过期，需要刷新或重新登录")
            End If
        End Sub

        Public Sub OnStop() Implements IPclModule.OnStop
            If _log IsNot Nothing Then _log.Info("微软登录模块已停止")
        End Sub

    End Class

End Namespace
