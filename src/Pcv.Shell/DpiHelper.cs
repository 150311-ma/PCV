using System;
using System.Runtime.InteropServices;

namespace Pcv.Shell
{
    /// <summary>
    /// 让进程"认识"高 DPI。
    ///
    /// 主要靠 app.manifest 声明；这里再做一次运行时兜底，防止 manifest 没被嵌进去
    /// （比如有人把这行 csproj 配置删了）。两个都失败也不会崩，只是界面会糊。
    ///
    /// 顺手记一笔：一度以为"150% 缩放下内容被裁"是 DPI 问题，其实是我截图时用了
    /// GetWindowRect 的虚拟化坐标——窗口本身没问题。真要看清楚，得乘 GetDpiForWindow/96。
    /// </summary>
    internal static class DpiHelper
    {
        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool SetProcessDpiAwarenessContext(IntPtr context);

        [DllImport("shcore.dll", SetLastError = true)]
        private static extern int SetProcessDpiAwareness(int awareness);

        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool SetProcessDPIAware();

        private static readonly IntPtr PerMonitorAwareV2 = new IntPtr(-4);

        public static void Enable()
        {
            try
            {
                if (SetProcessDpiAwarenessContext(PerMonitorAwareV2)) return;
            }
            catch
            {
                // Win10 1703 之前没有这个 API
            }

            try
            {
                if (SetProcessDpiAwareness(2) == 0) return; // PROCESS_PER_MONITOR_DPI_AWARE
            }
            catch
            {
                // Win8.1 以下没有 shcore
            }

            try
            {
                SetProcessDPIAware();
            }
            catch
            {
                // 全失败就按系统默认来，界面会糊但不会崩
            }
        }
    }
}
