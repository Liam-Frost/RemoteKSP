using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Security.Principal;
using System.Text;

namespace RemoteKSP.Server.Services;

/// <summary>
/// 服务（SYSTEM，session 0）在用户会话里启动「RemoteKSP.Server.exe --notify-pairing」显示通知。
/// 只发给本机管理员（只有他们能批准配对）；进程用该用户的普通令牌运行，不提权：
/// exe 清单要求管理员，用 __COMPAT_LAYER=RunAsInvoker 让它以调用者权限启动
/// </summary>
internal static unsafe partial class UserSessionLauncher
{
    private const int TokenGroups = 2;
    private const uint CREATE_NO_WINDOW = 0x08000000;
    private const uint CREATE_UNICODE_ENVIRONMENT = 0x00000400;

    /// <summary>在每个已登录管理员的会话里启动通知进程；会话里已经开着服务端 GUI 的跳过（GUI 自己会弹窗）。返回启动的个数</summary>
    public static int NotifyPairing(PairingToastContent content)
    {
        var exe = ServiceProgram.InstalledExe;
        if (!File.Exists(exe))
            return 0;

        var guiSessions = Process.GetProcessesByName(Path.GetFileNameWithoutExtension(exe))
            .Select(p => { using (p) return p.SessionId; })
            .ToHashSet();
        var commandLine = $"\"{exe}\" --notify-pairing {content.Encode()}";

        var launched = 0;
        foreach (var sessionId in SessionMessenger.ActiveSessions())
        {
            if (guiSessions.Contains(sessionId) || !WTSQueryUserToken(sessionId, out var token))
                continue;
            try
            {
                if (IsAdministrator(token) && Launch(token, exe, commandLine))
                    launched++;
            }
            finally
            {
                CloseHandle(token);
            }
        }
        return launched;
    }

    /// <summary>
    /// 令牌的组里有 BUILTIN\Administrators 就算管理员。UAC 下的受限令牌里它是「仅拒绝」组，
    /// 同样在列表里，所以不用再去取关联的完整令牌
    /// </summary>
    private static bool IsAdministrator(IntPtr token)
    {
        GetTokenInformation(token, TokenGroups, IntPtr.Zero, 0, out var length);
        if (length == 0)
            return false;
        var buffer = Marshal.AllocHGlobal(length);
        try
        {
            if (!GetTokenInformation(token, TokenGroups, buffer, length, out _))
                return false;
            var count = Marshal.ReadInt32(buffer);
            var groups = buffer + IntPtr.Size;   // TOKEN_GROUPS: DWORD GroupCount，然后按指针对齐的 SID_AND_ATTRIBUTES[]
            var admins = new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null);
            for (var i = 0; i < count; i++)
            {
                var sid = Marshal.ReadIntPtr(groups + i * 2 * IntPtr.Size);
                if (new SecurityIdentifier(sid) == admins)
                    return true;
            }
            return false;
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }
}
