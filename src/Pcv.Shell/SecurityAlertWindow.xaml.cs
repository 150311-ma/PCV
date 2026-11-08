using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace Pcv.Shell
{
    /// <summary>
    /// 安全提醒窗口：把检出说清楚，然后只有一个出口 —— 结束进程。
    ///
    /// 为什么不用 MessageBox：系统对话框会跟着系统主题走，和 PCV 的界面
    /// 不是一个样子；而且这里要显示多段详情和固定文案。
    /// </summary>
    public partial class SecurityAlertWindow : Window
    {
        internal SecurityAlertWindow(IReadOnlyList<Threat> threats)
        {
            InitializeComponent();

            var first = threats != null && threats.Count > 0 ? threats[0] : null;
            LabSummary.Text = threats != null && threats.Count > 1
                ? "共发现 " + threats.Count + " 项异常，其中第一项如下。"
                : (first != null ? "发现 1 项异常。" : "发现异常。");

            if (threats == null) return;
            foreach (var threat in threats)
            {
                if (threat == null) continue;

                var card = new Border
                {
                    Background = Trm.Surface,
                    BorderBrush = Trm.Hairline,
                    BorderThickness = new Thickness(1),
                    CornerRadius = new CornerRadius(11),
                    Padding = new Thickness(14, 12, 14, 12),
                    Margin = new Thickness(0, 0, 0, 10),
                    HorizontalAlignment = HorizontalAlignment.Stretch
                };

                var content = new StackPanel();
                content.Children.Add(new TextBlock
                {
                    Text = threat.Title,
                    FontSize = 13,
                    FontWeight = FontWeights.SemiBold,
                    Foreground = Trm.Ink,
                    TextWrapping = TextWrapping.Wrap
                });
                content.Children.Add(new TextBlock
                {
                    Text = threat.Detail,
                    FontSize = 12.5,
                    Foreground = Trm.Help,
                    TextWrapping = TextWrapping.Wrap,
                    LineHeight = 19,
                    Margin = new Thickness(0, 6, 0, 0)
                });

                card.Child = content;
                ListIssues.Children.Add(card);
            }
        }

        private void BtnConfirm_Click(object sender, RoutedEventArgs e)
        {
            // 只有确定这一个按钮：窗口本身也不给「关掉就当没事」的路。
            // 标题栏是 WindowStyle=None，没有 X；Alt+F4 也走同一条路。
            SecurityGuard.Terminate();
        }
    }
}
