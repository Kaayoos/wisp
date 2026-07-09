using System;
using System.Diagnostics;
using System.Runtime.InteropServices;

namespace Wisp.Services
{
    /// <summary>
    /// Assigns our child ffmpeg processes to a Windows Job Object that is configured to kill every
    /// member when the last handle to the job closes. Because that handle is owned by THIS process,
    /// the OS terminates all tracked ffmpeg instances the moment Wisp exits - clean quit, crash, or
    /// hard kill alike. This is what prevents the "ffmpeg left running with no way to stop until
    /// reboot" problem: a leaked recorder can no longer outlive the app.
    /// </summary>
    internal static class ChildProcessTracker
    {
        private static readonly IntPtr _jobHandle;
        private static readonly bool _available;

        static ChildProcessTracker()
        {
            try
            {
                _jobHandle = CreateJobObject(IntPtr.Zero, null);
                if (_jobHandle == IntPtr.Zero)
                {
                    _available = false;
                    return;
                }

                var extended = new JOBOBJECT_EXTENDED_LIMIT_INFORMATION
                {
                    BasicLimitInformation = new JOBOBJECT_BASIC_LIMIT_INFORMATION
                    {
                        LimitFlags = JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE
                    }
                };

                int length = Marshal.SizeOf<JOBOBJECT_EXTENDED_LIMIT_INFORMATION>();
                IntPtr infoPtr = Marshal.AllocHGlobal(length);
                try
                {
                    Marshal.StructureToPtr(extended, infoPtr, false);
                    _available = SetInformationJobObject(_jobHandle, JobObjectExtendedLimitInformation, infoPtr, (uint)length);
                }
                finally
                {
                    Marshal.FreeHGlobal(infoPtr);
                }
            }
            catch
            {
                _available = false;
            }
        }

        /// <summary>Best-effort: enroll a freshly started process so it dies with us. Safe to call always.</summary>
        public static void AddProcess(Process? process)
        {
            try
            {
                if (!_available || process == null || process.HasExited) return;
                AssignProcessToJobObject(_jobHandle, process.Handle);
            }
            catch
            {
                // A failure here only means this single process won't be auto-reaped; not fatal.
            }
        }

        private const uint JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE = 0x2000;
        private const int JobObjectExtendedLimitInformation = 9;

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
        private static extern IntPtr CreateJobObject(IntPtr lpJobAttributes, string? name);

        [DllImport("kernel32.dll")]
        private static extern bool SetInformationJobObject(IntPtr hJob, int infoType, IntPtr lpJobObjectInfo, uint cbJobObjectInfoLength);

        [DllImport("kernel32.dll")]
        private static extern bool AssignProcessToJobObject(IntPtr hJob, IntPtr hProcess);

        [StructLayout(LayoutKind.Sequential)]
        private struct JOBOBJECT_BASIC_LIMIT_INFORMATION
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
        private struct IO_COUNTERS
        {
            public ulong ReadOperationCount;
            public ulong WriteOperationCount;
            public ulong OtherOperationCount;
            public ulong ReadTransferCount;
            public ulong WriteTransferCount;
            public ulong OtherTransferCount;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct JOBOBJECT_EXTENDED_LIMIT_INFORMATION
        {
            public JOBOBJECT_BASIC_LIMIT_INFORMATION BasicLimitInformation;
            public IO_COUNTERS IoInfo;
            public UIntPtr ProcessMemoryLimit;
            public UIntPtr JobMemoryLimit;
            public UIntPtr PeakProcessMemoryUsed;
            public UIntPtr PeakJobMemoryUsed;
        }
    }
}
