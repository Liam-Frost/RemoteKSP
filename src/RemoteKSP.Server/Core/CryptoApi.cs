using System.Runtime.InteropServices;
using System.Text;

namespace RemoteKSP.Server.Core;

/// <summary>CryptoAPI 互操作</summary>
internal static partial class CryptoApi
{
    private const uint PROV_RSA_FULL = 1;
    private const uint CRYPT_VERIFYCONTEXT = 0xF0000000;
    private const uint CRYPT_SILENT = 0x00000040;
    private const uint CRYPT_FIRST = 1;
    private const uint PP_ENUMCONTAINERS = 2;

    [LibraryImport("advapi32.dll", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool CryptAcquireContextW(
        out IntPtr phProv, string? pszContainer, string? pszProvider,
        uint dwProvType, uint dwFlags);

    [LibraryImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool CryptReleaseContext(IntPtr hProv, uint dwFlags);

    [LibraryImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool CryptGetProvParam(
        IntPtr hProv, uint dwParam, byte[]? pbData, ref uint pdwDataLen, uint dwFlags);

    /// <summary>枚举 CSP 密钥容器</summary>
    public static List<string> EnumerateContainers(string providerName)
    {
        var containers = new List<string>();

        if (!CryptAcquireContextW(out var hProv, null, providerName,
            PROV_RSA_FULL, CRYPT_VERIFYCONTEXT | CRYPT_SILENT))
        {
            return containers;
        }

        try
        {
            uint flags = CRYPT_FIRST;
            uint dataLen = 0;

            while (true)
            {
                // 获取容器名长度
                if (!CryptGetProvParam(hProv, PP_ENUMCONTAINERS, null, ref dataLen, flags))
                    break;

                var buffer = new byte[dataLen];
                if (!CryptGetProvParam(hProv, PP_ENUMCONTAINERS, buffer, ref dataLen, flags))
                    break;

                // PP_ENUMCONTAINERS 返回以 null 结尾的 ANSI 字符串（不是 UTF-16）
                var nameLen = Array.IndexOf(buffer, (byte)0, 0, (int)dataLen);
                if (nameLen < 0) nameLen = (int)dataLen;
                var containerName = Encoding.Default.GetString(buffer, 0, nameLen);
                containers.Add(containerName);

                flags = 0; // 后续调用使用 CRYPT_NEXT
            }
        }
        finally
        {
            CryptReleaseContext(hProv, 0);
        }

        return containers;
    }
}
