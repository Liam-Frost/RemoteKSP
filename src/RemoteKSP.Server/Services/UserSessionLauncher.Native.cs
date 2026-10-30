using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;

namespace RemoteKSP.Server.Services;

internal static unsafe partial class UserSessionLauncher
{
    [StructLayout(LayoutKind.Sequential)]
    private struct STARTUPINFOW
    {
        public int cb;
        public IntPtr lpReserved, lpDesktop, lpTitle;
        public int dwX, dwY, dwXSize, dwYSize, dwXCountChars, dwYCountChars, dwFillAttribute;
        public uint dwFlags;
        public short wShowWindow, cbReserved2;
        public IntPtr lpReserved2, hStdInput, hStdOutput, hStdError;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct PROCESS_INFORMATION
    {
        public IntPtr hProcess, hThread;
        public int dwProcessId, dwThreadId;
    }

    private static bool Launch(IntPtr token, string exe, string commandLine)
    {
        if (!CreateEnvironmentBlock(out var userEnvironment, token, false))
            return false;
        var environment = IntPtr.Zero;
        var desktop = Marshal.StringToHGlobalUni(@"winsta0\default");
        try
        {
            environment = Marshal.StringToHGlobalUni(WithRunAsInvoker(ReadEnvironment(userEnvironment)));
            var startup = new STARTUPINFOW { cb = sizeof(STARTUPINFOW), lpDesktop = desktop };
            var cmd = (commandLine + "\0").ToCharArray();   // CreateProcessW 可能改写命令行缓冲区
            fixed (char* cmdPtr = cmd)
            {
                if (!CreateProcessAsUserW(token, exe, cmdPtr, IntPtr.Zero, IntPtr.Zero, false,
                        CREATE_NO_WINDOW | CREATE_UNICODE_ENVIRONMENT, environment, Path.GetDirectoryName(exe)!,
                        ref startup, out var info))
                    return false;
                CloseHandle(info.hThread);
                CloseHandle(info.hProcess);
                return true;
            }
        }
        finally
        {
            Marshal.FreeHGlobal(desktop);
            if (environment != IntPtr.Zero)
                Marshal.FreeHGlobal(environment);
            DestroyEnvironmentBlock(userEnvironment);
        }
    }

    /// <summary>环境块：以 \0 分隔、以空串结尾的 UTF-16 字符串</summary>
    private static List<string> ReadEnvironment(IntPtr block)
    {
        var result = new List<string>();
        for (var p = (char*)block; *p != '\0';)
        {
            var entry = new string(p);
            result.Add(entry);
            p += entry.Length + 1;
        }
        return result;
    }

    /// <summary>加上 __COMPAT_LAYER=RunAsInvoker（替换已有的），按名称排序后拼成环境块</summary>
    internal static string WithRunAsInvoker(IEnumerable<string> entries) =>
        string.Join('\0', entries
            .Where(e => !e.StartsWith("__COMPAT_LAYER=", StringComparison.OrdinalIgnoreCase))
            .Append("__COMPAT_LAYER=RunAsInvoker")
            .OrderBy(e => e, StringComparer.OrdinalIgnoreCase)) + "\0\0";

    [LibraryImport("wtsapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool WTSQueryUserToken(int sessionId, out IntPtr token);

    [LibraryImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetTokenInformation(IntPtr token, int infoClass, IntPtr info, int length, out int returnLength);

    [LibraryImport("userenv.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool CreateEnvironmentBlock(out IntPtr environment, IntPtr token,
        [MarshalAs(UnmanagedType.Bool)] bool inherit);

    [LibraryImport("userenv.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool DestroyEnvironmentBlock(IntPtr environment);

    [LibraryImport("advapi32.dll", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool CreateProcessAsUserW(IntPtr token, string application, char* commandLine,
        IntPtr processAttributes, IntPtr threadAttributes, [MarshalAs(UnmanagedType.Bool)] bool inheritHandles,
        uint creationFlags, IntPtr environment, string currentDirectory, ref STARTUPINFOW startupInfo,
        out PROCESS_INFORMATION processInformation);

    [LibraryImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool CloseHandle(IntPtr handle);
}
