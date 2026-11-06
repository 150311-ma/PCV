using System;
using System.Runtime.InteropServices;
using System.Threading;

namespace Pcv.Shell
{
    /// <summary>
    /// 直接走 Win32 剪贴板，不用 WPF 的 Clipboard.SetDataObject。
    ///
    /// 原因：在这台机器上 WPF 那条路一直抛 CLIPBRD_E_CANT_OPEN（OLE 初始化与消息泵的时序问题），
    /// 而 Win32 的 OpenClipboard / SetClipboardData 当场就能过。同一台机器实测的差别，不是理论。
    /// 失败会重试几次——剪贴板偶尔会被别的程序短暂占用。
    /// </summary>
    internal static class ClipboardWriter
    {
        private const uint CfUnicodeText = 13;
        private const uint GmemMoveable = 0x0002;

        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool OpenClipboard(IntPtr hWndNewOwner);

        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool EmptyClipboard();

        [DllImport("user32.dll", SetLastError = true)]
        private static extern IntPtr SetClipboardData(uint uFormat, IntPtr hMem);

        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool CloseClipboard();

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern IntPtr GlobalAlloc(uint uFlags, UIntPtr dwBytes);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern IntPtr GlobalLock(IntPtr hMem);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool GlobalUnlock(IntPtr hMem);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern IntPtr GlobalFree(IntPtr hMem);

        public static bool TrySetText(string text, int attempts = 5)
        {
            if (string.IsNullOrEmpty(text)) return false;

            for (var i = 0; i < attempts; i++)
            {
                if (TryOnce(text)) return true;
                Thread.Sleep(80);
            }
            return false;
        }

        private static bool TryOnce(string text)
        {
            IntPtr handle = IntPtr.Zero;
            var opened = false;
            try
            {
                if (!OpenClipboard(IntPtr.Zero)) return false;
                opened = true;

                if (!EmptyClipboard()) return false;

                var bytes = (text.Length + 1) * 2; // UTF-16 + 结尾的 \0
                handle = GlobalAlloc(GmemMoveable, (UIntPtr)bytes);
                if (handle == IntPtr.Zero) return false;

                var target = GlobalLock(handle);
                if (target == IntPtr.Zero) return false;
                try
                {
                    Marshal.Copy(text.ToCharArray(), 0, target, text.Length);
                    Marshal.WriteInt16(target, text.Length * 2, 0);
                }
                finally
                {
                    GlobalUnlock(handle);
                }

                if (SetClipboardData(CfUnicodeText, handle) == IntPtr.Zero) return false;
                handle = IntPtr.Zero; // 交给系统了，不能再 Free
                return true;
            }
            catch
            {
                return false;
            }
            finally
            {
                if (handle != IntPtr.Zero) GlobalFree(handle);
                if (opened) CloseClipboard();
            }
        }
    }
}
