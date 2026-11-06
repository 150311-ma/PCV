using System;
using System.Runtime.InteropServices;

namespace Pcv.Modular.Runtime
{
    /// <summary>
    /// 用 Windows 作业对象（Job Object）给框架进程上一道"回收绳"。
    ///
    /// 能做的事（都不需要管理员权限）：
    ///   · 宿主退出/崩溃时，作业里所有进程跟着死 —— 不留看不见的后台进程；
    ///   · 限制整个框架树的内存，防止它把机器吃光；
    ///   · 限制同时存在的进程数，挡住最简单的 fork 炸弹。
    ///
    /// 不能做的事：挡住它读宿主的文件。那需要 AppContainer 或低权限账户，
    /// 而没有管理员权限做不到。这里不假装做到了。
    /// </summary>
    internal sealed class FrameworkJob : IDisposable
    {
        private const int JobObjectExtendedLimitInformation = 9;
        private const uint JobObjectLimitProcessMemory = 0x00000100;
        private const uint JobObjectLimitKillOnJobClose = 0x00002000;
        private const uint JobObjectLimitActiveProcess = 0x00000008;

        [StructLayout(LayoutKind.Sequential)]
        private struct IoCounters
        {
            public ulong ReadOperationCount;
            public ulong WriteOperationCount;
            public ulong OtherOperationCount;
            public ulong ReadTransferCount;
            public ulong WriteTransferCount;
            public ulong OtherTransferCount;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct BasicLimitInformation
        {
            public long PerProcessUserTimeLimit;
            public long PerJobUserTimeLimit;
            public uint LimitFlags;
            public UIntPtr MinimumWorkingSetSize;
            public UIntPtr MaximumWorkingSetSize;
            public uint ActiveProcessLimit;
            public UIntPtr Affinity;
            public uint PriorityClass;
            public uint SchedulingClass;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct ExtendedLimitInformation
        {
            public BasicLimitInformation BasicLimitInformation;
            public IoCounters IoInfo;
            public UIntPtr ProcessMemoryLimit;
            public UIntPtr JobMemoryLimit;
            public UIntPtr PeakProcessMemoryUsed;
            public UIntPtr PeakJobMemoryUsed;
        }

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern IntPtr CreateJobObject(IntPtr securityAttributes, string name);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool SetInformationJobObject(IntPtr job, int infoClass, ref ExtendedLimitInformation info, int length);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool AssignProcessToJobObject(IntPtr job, IntPtr process);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool CloseHandle(IntPtr handle);

        private IntPtr _handle = IntPtr.Zero;

        /// <summary>建一个作业并把它套到指定进程上。失败不抛异常 —— 没这道绳也要能跑。</summary>
        public bool Attach(IntPtr processHandle, long memoryLimitBytes, uint maxProcesses)
        {
            try
            {
                _handle = CreateJobObject(IntPtr.Zero, null);
                if (_handle == IntPtr.Zero) return false;

                var info = new ExtendedLimitInformation();
                info.BasicLimitInformation.LimitFlags =
                    JobObjectLimitKillOnJobClose | JobObjectLimitProcessMemory | JobObjectLimitActiveProcess;
                info.BasicLimitInformation.ActiveProcessLimit = maxProcesses;
                info.ProcessMemoryLimit = (UIntPtr)(ulong)memoryLimitBytes;

                var size = Marshal.SizeOf(typeof(ExtendedLimitInformation));
                if (!SetInformationJobObject(_handle, JobObjectExtendedLimitInformation, ref info, size))
                {
                    CloseHandle(_handle);
                    _handle = IntPtr.Zero;
                    return false;
                }

                if (!AssignProcessToJobObject(_handle, processHandle))
                {
                    CloseHandle(_handle);
                    _handle = IntPtr.Zero;
                    return false;
                }

                return true;
            }
            catch
            {
                if (_handle != IntPtr.Zero) CloseHandle(_handle);
                _handle = IntPtr.Zero;
                return false;
            }
        }

        public void Dispose()
        {
            if (_handle == IntPtr.Zero) return;
            // 关掉句柄 = 触发作业的 KILL_ON_JOB_CLOSE，作业里的进程跟着结束
            CloseHandle(_handle);
            _handle = IntPtr.Zero;
        }
    }
}
