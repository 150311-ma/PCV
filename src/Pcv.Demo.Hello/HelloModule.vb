Imports Pcv.Modular.Abstractions
Imports Pcv.Modular.Contracts

Namespace Global.Pcv.Demo.Hello

    ''' <summary>
    ''' 示例功能框架：用来验证"拖进来就能用"这套流程真的通。
    ''' 它本身不做什么大事，只在日志里报到，并把自己登记成一个服务。
    ''' </summary>
    Public Partial Class HelloModule
        Implements IPclModule, IFrameworkSurface

        Private _log As ILogger
        Private _ticks As Integer

        Public Sub OnLoad(context As IModuleContext) Implements IPclModule.OnLoad
            _log = context.Log
            _log.Info($"示例框架已装载：{context.Manifest.DisplayName} {context.Manifest.Version}，来自 {context.ModuleDirectory}")
        End Sub

        Public Sub OnStart() Implements IPclModule.OnStart
            _ticks += 1
            _log.Info($"示例框架已启动（第 {_ticks} 次），这一步能跑通说明包是真的装上了")
        End Sub

        Public Sub OnStop() Implements IPclModule.OnStop
            If _log IsNot Nothing Then _log.Info("示例框架已停止")
        End Sub

    End Class

End Namespace
