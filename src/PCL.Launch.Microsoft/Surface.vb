Imports System.Threading
Imports System.Windows
Imports System.Windows.Controls
Imports System.Windows.Media
Imports Pcv.Modular.Abstractions
Imports Pcv.Modular.Contracts

Namespace Global.PCL.Launch.Microsoft

    ''' <summary>
    ''' 微软登录框架的界面：显示账号状态，点按钮走设备码。
    ''' 登录要等浏览器授权，所以按钮点下去先禁用，回来再说结果——不给"点了没反应"的中间态。
    ''' </summary>
    Partial Public Class MicrosoftLaunchModule

        Public ReadOnly Property Title As String Implements IFrameworkSurface.Title
            Get
                Return "微软登录"
            End Get
        End Property

        Public Function CreateView(context As IModuleContext) As Object Implements IFrameworkSurface.CreateView
            Dim root As New StackPanel()

            Dim status As New TextBlock With {
                .Text = "正在读取账号状态…",
                .FontSize = 13,
                .TextWrapping = TextWrapping.Wrap,
                .Foreground = New SolidColorBrush(Color.FromRgb(&H34, &H3D, &H4A))
            }
            root.Children.Add(status)

            Dim hint As New TextBlock With {
                .FontSize = 12,
                .TextWrapping = TextWrapping.Wrap,
                .LineHeight = 20,
                .Margin = New Thickness(0, 10, 0, 0),
                .Foreground = New SolidColorBrush(Color.FromRgb(&H7C, &H87, &H98))
            }
            root.Children.Add(hint)

            Dim button As New Button With {
                .Content = "开始登录（设备码）",
                .Width = 190,
                .Height = 38,
                .Margin = New Thickness(0, 18, 0, 0),
                .HorizontalAlignment = HorizontalAlignment.Left,
                .Cursor = System.Windows.Input.Cursors.Hand
            }
            root.Children.Add(button)

            Dim service = _service

            Dim refresh As Action =
                Sub()
                    If service Is Nothing Then
                        status.Text = "登录服务没拿到。"
                        button.IsEnabled = False
                        Return
                    End If

                    If Not service.IsConfigured Then
                        status.Text = "没有配置 ClientId"
                        hint.Text = "开源版不带内置的 ClientId。设置环境变量 PCL_MS_CLIENT_ID，" &
                                    "或在配置里写 MsClientId，再打开这个界面。"
                        button.IsEnabled = False
                        Return
                    End If

                    Dim account = service.CachedAccount
                    If account Is Nothing Then
                        status.Text = "当前没有登录"
                        hint.Text = "点下面的按钮，浏览器里会给你一个代码，输进去就行。"
                    Else
                        status.Text = "已登录：" & account.Name
                        hint.Text = If(account.ExpiresAt > DateTimeOffset.UtcNow,
                                       "令牌有效期到 " & account.ExpiresAt.LocalDateTime.ToString("yyyy-MM-dd HH:mm"),
                                       "令牌已经过期了，重新登录一次。")
                    End If
                    button.IsEnabled = True
                End Sub

            AddHandler button.Click,
                Async Sub(sender As Object, e As RoutedEventArgs)
                    button.IsEnabled = False
                    Try
                        status.Text = "正在申请设备码…"
                        Dim device = Await service.RequestDeviceCodeAsync(CancellationToken.None)
                        status.Text = "在浏览器打开 " & device.VerificationUri
                        hint.Text = "代码：" & device.UserCode & "（" & device.ExpiresInSeconds & " 秒内有效）"

                        Dim progress As New Progress(Of String)(Sub(text) hint.Text = text)
                        Dim account = Await service.CompleteLoginAsync(device, progress, CancellationToken.None)

                        status.Text = "登录成功：" & account.Name
                        hint.Text = "UUID " & account.Uuid
                        context.Log.Info("界面里完成微软登录：" & account.Name)
                    Catch ex As Exception
                        status.Text = "登录失败"
                        hint.Text = ex.Message
                        context.Log.Error("界面里微软登录失败", ex)
                    Finally
                        refresh()
                    End Try
                End Sub

            refresh()
            Return root
        End Function

    End Class

End Namespace
