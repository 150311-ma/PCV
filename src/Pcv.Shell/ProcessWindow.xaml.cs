using System;
using System.Text;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using Pcv.Modular.Runtime;

namespace Pcv.Shell
{
    /// <summary>
    /// 进程型框架的窗口：它的输出在这，输入也在这。
    ///
    /// 这个窗口不假装自己是宿主界面——深色、只显示那个进程说的话。
    /// 用户看到的"独立窗口"就是它。
    /// </summary>
    public partial class ProcessWindow : Window
    {
        private readonly ProcessFramework _framework;
        private readonly StringBuilder _buffer = new StringBuilder();

        public ProcessWindow(ProcessFramework framework)
        {
            _framework = framework;
            InitializeComponent();

            AppTitle();
            framework.Output += OnOutput;
            framework.TitleChanged += (s, title) => Dispatcher.Invoke(() =>
            {
                Title = title + " · PCV";
                LabTitle.Text = title;
            });
            framework.Exited += (s, code) => Dispatcher.Invoke(() =>
            {
                Dot.Fill = new SolidColorBrush(Color.FromRgb(0x8B, 0x94, 0x9E));
                LabSub.Text = code == 0 ? "已退出" : $"已退出（{code}）";
                BtnStop.IsEnabled = false;
                Input.IsEnabled = false;
            });

            Closed += (s, e) =>
            {
                framework.Output -= OnOutput;
                // 关窗口就停进程：不留下看不见的后台进程
                if (framework.IsRunning) framework.Stop();
            };

            Loaded += (s, e) =>
            {
                GlassHelper.TryEnableRoundedCorners(this);
                Input.Focus();
            };
        }

        private void AppTitle()
        {
            Title = _framework.Title + " · PCV";
            LabTitle.Text = _framework.Title;
            LabSub.Text = _framework.Id;
        }

        private void OnOutput(ProcessFramework framework, string line)
        {
            Dispatcher.Invoke(() =>
            {
                _buffer.AppendLine(line);
                // 只留最近 2000 行，跑一天的框架也不会把内存吃光
                if (_buffer.Length > 200000) _buffer.Remove(0, _buffer.Length - 100000);

                Output.Text = _buffer.ToString();
                Scroller.ScrollToEnd();
            });
        }

        private void Input_KeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Enter) Send();
        }

        private void BtnSend_Click(object sender, RoutedEventArgs e) => Send();

        private void Send()
        {
            var text = Input.Text;
            Input.Clear();
            if (!_framework.IsRunning) return;

            _framework.SendText(text);
            _buffer.AppendLine("> " + text);
            Output.Text = _buffer.ToString();
            Scroller.ScrollToEnd();
        }

        private void BtnStop_Click(object sender, RoutedEventArgs e)
        {
            _framework.Stop();
            BtnStop.IsEnabled = false;
        }

        private void BtnClose_Click(object sender, RoutedEventArgs e) => Close();

        private void TitleBar_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            if (e.ClickCount == 2)
            {
                WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;
                return;
            }
            if (e.ButtonState == MouseButtonState.Pressed) DragMove();
        }
    }
}
