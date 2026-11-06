Imports System.Windows
Imports System.Windows.Controls
Imports System.Windows.Media
Imports Pcv.Modular.Abstractions
Imports Pcv.Modular.Contracts

Namespace Global.Pcv.Demo.Hello

    ''' <summary>示例框架的界面：一个按钮，点一下加一。够简单，能看出界面是活的。</summary>
    Partial Public Class HelloModule

        Public ReadOnly Property Title As String Implements IFrameworkSurface.Title
            Get
                Return "示例框架"
            End Get
        End Property

        Public Function CreateView(context As IModuleContext) As Object Implements IFrameworkSurface.CreateView
            Dim root As New StackPanel()
            Dim count As Integer = 0

            root.Children.Add(New TextBlock With {
                .Text = "这个界面是框架自己画的，不是宿主给的模板。",
                .FontSize = 12,
                .TextWrapping = TextWrapping.Wrap,
                .Foreground = New SolidColorBrush(Color.FromRgb(&H7C, &H87, &H98))
            })

            Dim counter As New TextBlock With {
                .Text = "点了 0 下",
                .FontSize = 26,
                .FontWeight = FontWeights.Bold,
                .Margin = New Thickness(0, 18, 0, 0),
                .Foreground = New SolidColorBrush(Color.FromRgb(&H13, &H70, &HF3))
            }
            root.Children.Add(counter)

            Dim button As New Button With {
                .Content = "点我",
                .Width = 110,
                .Height = 36,
                .Margin = New Thickness(0, 14, 0, 0),
                .HorizontalAlignment = HorizontalAlignment.Left,
                .Cursor = System.Windows.Input.Cursors.Hand
            }
            AddHandler button.Click,
                Sub(sender As Object, e As RoutedEventArgs)
                    count += 1
                    counter.Text = "点了 " & count & " 下"
                    context.Log.Info("示例界面上点了第 " & count & " 下")
                End Sub
            root.Children.Add(button)

            Return root
        End Function

    End Class

End Namespace
