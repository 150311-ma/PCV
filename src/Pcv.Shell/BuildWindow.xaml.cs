using System;
using System.Text;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using Pcv.Modular.Abstractions;

namespace Pcv.Shell
{
    /// <summary>
    /// 构建脚本的运行窗口：左边不用管，它就是个带进度和"装进 PCV"按钮的日志窗。
    /// </summary>
    public partial class BuildWindow : Window
    {
        private readonly ScriptBuilder _builder;
        private readonly string _scriptPath;
        private readonly Action<ScriptBuilder.Result> _onFinished;
        private readonly StringBuilder _buffer = new StringBuilder();

        public BuildWindow(ScriptBuilder builder, string scriptPath, Action<ScriptBuilder.Result> onFinished)
        {
            _builder = builder;
            _scriptPath = scriptPath;
            _onFinished = onFinished;
            InitializeComponent();

            LabScript.Text = System.IO.Path.GetFileName(scriptPath);
            Title = "构建框架包 · PCV";
            Loaded += (s, e) =>
            {
                GlassHelper.TryEnableRoundedCorners(this);
                Start();
            };
        }

        private void Start()
        {
            LabStatus.Text = "正在跑脚本…";
            System.Threading.Tasks.Task.Run(() =>
            {
                var result = _builder.Run(_scriptPath, line => Dispatcher.Invoke(() => Append(line)));
                Dispatcher.Invoke(() => Finish(result));
            });
        }

        private void Append(string line)
        {
            _buffer.AppendLine(line);
            if (_buffer.Length > 300000) _buffer.Remove(0, _buffer.Length - 150000);
            Output.Text = _buffer.ToString();
            Scroller.ScrollToEnd();
        }

        private void Finish(ScriptBuilder.Result result)
        {
            Append("");
            Append("── 结束 ──");
            Append(result.Message);
            if (result.ExitCode != 0) Append("工作目录（脚本和中间产物都在这）：" + result.WorkDirectory);

            LabStatus.Text = result.Message;
            if (result.Success)
            {
                Dot.Fill = new SolidColorBrush(Color.FromRgb(0x3D, 0xDC, 0x84));
                BtnInstall.IsEnabled = false;
                BtnInstall.Content = "正在自动安装…";

                // 拖进来就是要"装好"，不是要一个装着安装按钮的窗口
                InstallRequested?.Invoke(this, EventArgs.Empty);
            }
            else
            {
                Dot.Fill = new SolidColorBrush(Color.FromRgb(0xF8, 0x51, 0x49));
                BtnInstall.IsEnabled = false;
                BtnInstall.Content = "没装成";
            }

            _onFinished?.Invoke(result);
        }

        /// <summary>安装结束（成功或失败）时由外面回调，把窗口切到收尾状态。</summary>
        public void Installed(bool success, string message)
        {
            Append("");
            Append(message);
            LabStatus.Text = message;
            BtnInstall.IsEnabled = false;
            BtnInstall.Content = success ? "已装好" : "安装失败";
            BtnInstall.Visibility = Visibility.Visible;
            Dot.Fill = new SolidColorBrush(success
                ? Color.FromRgb(0x3D, 0xDC, 0x84)
                : Color.FromRgb(0xF8, 0x51, 0x49));

            // 装好了 1.5 秒自动关，不占着屏幕
            if (success)
            {
                var timer = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromMilliseconds(1500) };
                timer.Tick += (s, e) => { timer.Stop(); Close(); };
                timer.Start();
            }
        }

        private void BtnInstall_Click(object sender, RoutedEventArgs e)
        {
            BtnInstall.IsEnabled = false;
            BtnInstall.Content = "安装中…";
            InstallRequested?.Invoke(this, EventArgs.Empty);
        }

        public event EventHandler InstallRequested;

        private void BtnClose_Click(object sender, RoutedEventArgs e) => Close();

        private void TitleBar_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            if (e.ButtonState == MouseButtonState.Pressed) DragMove();
        }
    }
}
