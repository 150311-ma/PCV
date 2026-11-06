using System;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;

namespace Pcv.Shell
{
    /// <summary>
    /// 窗口外观辅助：圆角 + 拖拽。
    ///
    /// 关于"液态玻璃"的取舍，先说清楚：
    ///   GitHub 上主流两条路——TianXiaTech/BlurWindow 那类靠 SetWindowCompositionAttribute 让窗口真的透出桌面，
    ///   trrahul/glassmorphism-wpf 的 XAML 方案靠 BlurEffect + 半透明层 + 边缘高光"画出"玻璃感。
    ///   前者在 Win11 22H2 之后行为不稳定，且失败时会变成一块黑窗；
    ///   后者不依赖系统合成器，任何机器看起来都一样。
    ///   这里选后者（外加 Win11 原生圆角），因为"能稳定显示"比"理论上更真"重要。
    /// </summary>
    internal static class GlassHelper
    {
        private const int DwmwaWindowCornerPreference = 33;
        private const int DwmwcpRound = 2;

        [DllImport("dwmapi.dll", PreserveSig = true)]
        private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);

        /// <summary>Win11 原生圆角；Win10 上返回 false，不影响功能。</summary>
        public static bool TryEnableRoundedCorners(Window window)
        {
            try
            {
                var hwnd = new WindowInteropHelper(window).Handle;
                if (hwnd == IntPtr.Zero) return false;
                var preference = DwmwcpRound;
                return DwmSetWindowAttribute(hwnd, DwmwaWindowCornerPreference, ref preference, sizeof(int)) == 0;
            }
            catch
            {
                return false;
            }
        }
    }
}
