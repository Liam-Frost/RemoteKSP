using System;
using System.IO;
using System.Security.Cryptography;

namespace RemoteKSP.Server.Core;

/// <summary>DPAPI 数据保护辅助类</summary>
public static class DpapiHelper
{
    /// <summary>加密数据（机器范围）</summary>
    public static byte[] Protect(byte[] data)
    {
        return ProtectedData.Protect(data, null, DataProtectionScope.LocalMachine);
    }

    /// <summary>解密数据（机器范围）</summary>
    public static byte[] Unprotect(byte[] encryptedData)
    {
        return ProtectedData.Unprotect(encryptedData, null, DataProtectionScope.LocalMachine);
    }

    /// <summary>加密字符串并返回 base64</summary>
    public static string ProtectString(string plaintext)
    {
        var bytes = System.Text.Encoding.UTF8.GetBytes(plaintext);
        var encrypted = Protect(bytes);
        return Convert.ToBase64String(encrypted);
    }

    /// <summary>从 base64 解密字符串</summary>
    public static string UnprotectString(string base64)
    {
        var encrypted = Convert.FromBase64String(base64);
        var bytes = Unprotect(encrypted);
        return System.Text.Encoding.UTF8.GetString(bytes);
    }

    /// <summary>保存 PIN 到文件（DPAPI 加密）</summary>
    public static void SavePin(string pin)
    {
        ServerPaths.EnsureDataDirectory();
        var encrypted = ProtectString(pin);
        File.WriteAllText(ServerPaths.PinFile, encrypted);
    }

    /// <summary>从文件加载 PIN</summary>
    public static string? LoadPin()
    {
        if (!File.Exists(ServerPaths.PinFile))
            return null;

        try
        {
            var encrypted = File.ReadAllText(ServerPaths.PinFile);
            return UnprotectString(encrypted);
        }
        catch
        {
            return null;
        }
    }

    /// <summary>清除 PIN 文件</summary>
    public static void ClearPin()
    {
        if (File.Exists(ServerPaths.PinFile))
            File.Delete(ServerPaths.PinFile);
    }
}
