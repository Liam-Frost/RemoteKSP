using System;
using System.Runtime.InteropServices;

namespace RemoteKSP.Server.Services;

/// <summary>
/// 服务以 SYSTEM 运行在 session 0，不能直接弹窗；用 WTSSendMessage 给所有已登录的会话（控制台和 RDP）发消息框。
/// 不等待用户点击，签名请求不会被阻塞。
/// </summary>
internal static partial class SessionMessenger
{
    private const int WTSActive = 0;
    private const int MB_OK = 0x0;
    private const int MB_ICONERROR = 0x10;
    private const int MB_SETFOREGROUND = 0x10000;
    private const int MB_TOPMOST = 0x40000;

    [StructLayout(LayoutKind.Sequential)]
    private struct WTS_SESSION_INFOW
    {
        public int SessionId;
        public IntPtr pWinStationName;
        public int State;
    }

    [LibraryImport("wtsapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool WTSEnumerateSessionsW(IntPtr hServer, int reserved, int version,
        out IntPtr ppSessionInfo, out int pCount);

    [LibraryImport("wtsapi32.dll")]
    private static partial void WTSFreeMemory(IntPtr pMemory);

    [LibraryImport("wtsapi32.dll", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool WTSSendMessageW(IntPtr hServer, int sessionId,
        string pTitle, int titleLength, string pMessage, int messageLength,
        int style, int timeout, out int pResponse, [MarshalAs(UnmanagedType.Bool)] bool bWait);

    /// <summary>发给所有活动会话；返回送达的会话数（没人登录时为 0）</summary>
    public static int Broadcast(string title, string message)
    {
        if (!WTSEnumerateSessionsW(IntPtr.Zero, 0, 1, out var sessions, out var count))
            return 0;

        var sent = 0;
        try
        {
            var size = Marshal.SizeOf<WTS_SESSION_INFOW>();
            for (var i = 0; i < count; i++)
            {
                var info = Marshal.PtrToStructure<WTS_SESSION_INFOW>(sessions + i * size);
                // session 0 是服务会话，没有用户桌面
                if (info.State != WTSActive || info.SessionId == 0)
                    continue;
                if (WTSSendMessageW(IntPtr.Zero, info.SessionId,
                        title, title.Length * sizeof(char), message, message.Length * sizeof(char),
                        MB_OK | MB_ICONERROR | MB_SETFOREGROUND | MB_TOPMOST, 0, out _, false))
                    sent++;
            }
        }
        finally
        {
            WTSFreeMemory(sessions);
        }
        return sent;
    }
}
