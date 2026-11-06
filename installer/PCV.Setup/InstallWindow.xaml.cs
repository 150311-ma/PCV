using System;
using System.Diagnostics;
using System.IO;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Input;
using Microsoft.Win32;

namespace PCV.Setup
{
    public partial class InstallWindow : Window
    {
        public InstallWindow()
        {
            InitializeComponent();
            LabVersion.Text = Installer.Version + " · 简称 PCV · 一个只负责装框架的壳";
            BoxPath.Text = Installer.DefaultTarget;
        }

        private void BtnBrowse_Click(object sender, RoutedEventArgs e)
        {
            var dialog = new SaveFileDialog
            {
                Title = "选一个安装目录",
                FileName = "选择此文件夹",
                Filter = "文件夹|*.folder",
                CheckPathExists = true
            };
            try
            {
                // 用一个假文件名拿到"选目录"的效果，取消时不会动路径
                if (dialog.ShowDialog(this) == true)
                    BoxPath.Text = Path.GetDirectoryName(dialog.FileName);
            }
            catch
            {
                // 用户取消
            }
        }

        private async void BtnInstall_Click(object sender, RoutedEventArgs e)
        {
            var target = (BoxPath.Text ?? "").Trim();
            if (target.Length == 0)
            {
                LabStatus.Text = "先填一个目录。";
                return;
            }

            BtnInstall.IsEnabled = false;
            BtnCancel.IsEnabled = false;
            BoxPath.IsEnabled = false;
            Bar.Value = 8;

            var progress = new Progress<string>(text =>
            {
                LabStatus.Text = text;
                Bar.Value = Math.Min(92, Bar.Value + 14);
            });

            var result = await Task.Run(() => Installer.Install(target, true, text =>
                ((IProgress<string>)progress).Report(text)));

            Bar.Value = 100;
            LabStatus.Text = result.Message;

            if (!result.Success)
            {
                BtnInstall.IsEnabled = true;
                BtnCancel.IsEnabled = true;
                BoxPath.IsEnabled = true;
                return;
            }

            PanDone.Visibility = Visibility.Visible;
            LabTarget.Text = "位置：" + result.Target;
            BtnInstall.Content = "完成";
            BtnInstall.IsEnabled = true;
            BtnInstall.Click -= BtnInstall_Click;
            BtnInstall.Click += BtnFinish_Click;
            BtnCancel.Visibility = Visibility.Collapsed;
        }

        private void BtnFinish_Click(object sender, RoutedEventArgs e)
        {
            if (CheckLaunch.IsChecked == true)
            {
                try
                {
                    var exe = Path.Combine(Installer.DefaultTarget, Installer.ExeName);
                    var path = Path.Combine((BoxPath.Text ?? Installer.DefaultTarget).Trim(), Installer.ExeName);
                    Process.Start(new ProcessStartInfo(File.Exists(path) ? path : exe) { UseShellExecute = true });
                }
                catch
                {
                    // 起不来就算了，快捷方式还在
                }
            }
            Close();
        }

        private void BtnCancel_Click(object sender, RoutedEventArgs e) => Close();

        private void BtnClose_Click(object sender, RoutedEventArgs e) => Close();

        private void TitleBar_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            if (e.ButtonState == MouseButtonState.Pressed) DragMove();
        }
    }
}
