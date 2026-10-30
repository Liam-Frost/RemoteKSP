using System.ComponentModel;
using System.Runtime.InteropServices;

namespace RemoteKSP.Server.Services;

/// <summary>
/// 配置 Windows 服务的失败恢复策略（等同 sc failure + sc failureflag）。
/// 进程崩溃，或服务以非零退出码停止时，SCM 按策略自动重启服务。
/// </summary>
public static class ServiceRecovery
{
    /// <summary>失败计数清零的间隔：一天内没有再失败，下次失败重新从第一次的延迟开始</summary>
    private const uint ResetPeriodSeconds = 24 * 60 * 60;

    /// <summary>第 1、2、3 次及之后失败的重启延迟</summary>
    private static readonly uint[] RestartDelaysMs = [5_000, 10_000, 60_000];

    private const uint SC_MANAGER_CONNECT = 0x0001;
    private const uint SERVICE_CHANGE_CONFIG = 0x0002;
    private const uint SERVICE_START = 0x0010;
    private const uint SERVICE_CONFIG_FAILURE_ACTIONS = 2;
    private const uint SERVICE_CONFIG_FAILURE_ACTIONS_FLAG = 4;
    private const int SC_ACTION_RESTART = 1;

    [StructLayout(LayoutKind.Sequential)]
    private struct SC_ACTION
    {
        public int Type;
        public uint Delay;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct SERVICE_FAILURE_ACTIONS
    {
        public uint dwResetPeriod;
        public string? lpRebootMsg;
        public string? lpCommand;
        public uint cActions;
        public IntPtr lpsaActions;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct SERVICE_FAILURE_ACTIONS_FLAG
    {
        public int fFailureActionsOnNonCrashFailures;
    }

    [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern IntPtr OpenSCManagerW(string? machine, string? database, uint access);

    [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern IntPtr OpenServiceW(IntPtr scm, string name, uint access);

    [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool ChangeServiceConfig2W(IntPtr service, uint infoLevel, ref SERVICE_FAILURE_ACTIONS info);

    [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool ChangeServiceConfig2W(IntPtr service, uint infoLevel, ref SERVICE_FAILURE_ACTIONS_FLAG info);

    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern bool CloseServiceHandle(IntPtr handle);

    /// <summary>写入恢复策略；需要管理员或 SYSTEM 权限。失败抛 Win32Exception</summary>
    public static void Configure(string serviceName)
    {
        var scm = OpenSCManagerW(null, null, SC_MANAGER_CONNECT);
        if (scm == IntPtr.Zero)
            throw new Win32Exception(Marshal.GetLastWin32Error(), "无法连接服务控制管理器");
        try
        {
            // SC_ACTION_RESTART 要求句柄带 SERVICE_START 权限
            var service = OpenServiceW(scm, serviceName, SERVICE_CHANGE_CONFIG | SERVICE_START);
            if (service == IntPtr.Zero)
                throw new Win32Exception(Marshal.GetLastWin32Error(), $"无法打开服务 {serviceName}");
            try
            {
                SetFailureActions(service);

                // 没有这个标志时，只有进程崩溃才算失败；服务以非零退出码正常停止不会触发重启
                var flag = new SERVICE_FAILURE_ACTIONS_FLAG { fFailureActionsOnNonCrashFailures = 1 };
                if (!ChangeServiceConfig2W(service, SERVICE_CONFIG_FAILURE_ACTIONS_FLAG, ref flag))
                    throw new Win32Exception(Marshal.GetLastWin32Error(), "设置失败恢复标志失败");
            }
            finally
            {
                CloseServiceHandle(service);
            }
        }
        finally
        {
            CloseServiceHandle(scm);
        }
    }

    private static void SetFailureActions(IntPtr service)
    {
        var size = Marshal.SizeOf<SC_ACTION>();
        var buffer = Marshal.AllocHGlobal(size * RestartDelaysMs.Length);
        try
        {
            for (var i = 0; i < RestartDelaysMs.Length; i++)
                Marshal.StructureToPtr(new SC_ACTION { Type = SC_ACTION_RESTART, Delay = RestartDelaysMs[i] },
                    buffer + i * size, false);

            var actions = new SERVICE_FAILURE_ACTIONS
            {
                dwResetPeriod = ResetPeriodSeconds,
                cActions = (uint)RestartDelaysMs.Length,
                lpsaActions = buffer
            };
            if (!ChangeServiceConfig2W(service, SERVICE_CONFIG_FAILURE_ACTIONS, ref actions))
                throw new Win32Exception(Marshal.GetLastWin32Error(), "设置服务失败恢复策略失败");
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }
}
