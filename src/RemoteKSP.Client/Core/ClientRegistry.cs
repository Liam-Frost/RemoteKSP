using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Win32;

namespace RemoteKSP.Client.Core;

/// <summary>已配对的服务端（HKLM\SOFTWARE\RemoteKSP\Servers\{Id}）</summary>
public record ServerEntry(string Id, string ServerUrl, string ServerName, string ClientId, string Secret,
    string? TlsThumbprint);

/// <summary>证书 → 服务端映射（HKLM\SOFTWARE\RemoteKSP\Certificates\{Thumbprint}）</summary>
public record CertificateBinding(string Thumbprint, string ServerId, string Name, DateTime AddedAt, bool IsTest);

/// <summary>
/// 客户端注册表。Native KSP（native/ksp）直接读取这里的结构，字段名和类型必须保持一致。
/// 所有写操作需要管理员权限（客户端 GUI 以管理员运行）。
/// </summary>
public static class ClientRegistry
{
    private const string RootPath = @"SOFTWARE\RemoteKSP";
    private const string ServersPath = RootPath + @"\Servers";
    private const string CertificatesPath = RootPath + @"\Certificates";

    // ===== 服务端 =====

    public static List<ServerEntry> LoadServers()
    {
        using var key = Registry.LocalMachine.OpenSubKey(ServersPath, false);
        if (key == null)
            return new List<ServerEntry>();

        return key.GetSubKeyNames()
            .OrderBy(n => n, StringComparer.OrdinalIgnoreCase)
            .Select(LoadServer)
            .Where(s => s != null)
            .Cast<ServerEntry>()
            .ToList();
    }

    public static ServerEntry? LoadServer(string id)
    {
        using var key = Registry.LocalMachine.OpenSubKey($@"{ServersPath}\{id}", false);
        if (key == null)
            return null;

        var url = key.GetValue("ServerUrl") as string;
        var clientId = key.GetValue("ClientId") as string;
        var secret = Unprotect(key.GetValue("SecretProtected") as byte[]);
        if (string.IsNullOrEmpty(url) || string.IsNullOrEmpty(clientId) || secret == null)
            return null;

        return new ServerEntry(id, url, key.GetValue("ServerName") as string ?? url, clientId, secret,
            key.GetValue("TlsThumbprint") as string);
    }

    public static ServerEntry? FindServerByUrl(string serverUrl) =>
        LoadServers().FirstOrDefault(s => string.Equals(s.ServerUrl.TrimEnd('/'), serverUrl.TrimEnd('/'),
            StringComparison.OrdinalIgnoreCase));

    public static void SaveServer(ServerEntry server)
    {
        using var key = Registry.LocalMachine.CreateSubKey($@"{ServersPath}\{server.Id}", true)
            ?? throw new InvalidOperationException("无法写入注册表（需要管理员权限）");
        key.SetValue("ServerUrl", server.ServerUrl, RegistryValueKind.String);
        key.SetValue("ServerName", server.ServerName, RegistryValueKind.String);
        key.SetValue("ClientId", server.ClientId, RegistryValueKind.String);
        key.SetValue("SecretProtected", Protect(server.Secret), RegistryValueKind.Binary);
        if (!string.IsNullOrEmpty(server.TlsThumbprint))
            key.SetValue("TlsThumbprint", server.TlsThumbprint, RegistryValueKind.String);
    }

    public static void RenameServer(string id, string name)
    {
        using var key = Registry.LocalMachine.OpenSubKey($@"{ServersPath}\{id}", true)
            ?? throw new InvalidOperationException("服务端不存在");
        key.SetValue("ServerName", name, RegistryValueKind.String);
    }

    public static void DeleteServer(string id)
    {
        Registry.LocalMachine.DeleteSubKeyTree($@"{ServersPath}\{id}", false);
    }

    /// <summary>下一个可用 ID：server-001, server-002, ...</summary>
    public static string NextServerId()
    {
        using var key = Registry.LocalMachine.OpenSubKey(ServersPath, false);
        var max = key?.GetSubKeyNames()
            .Select(n => n.StartsWith("server-") && int.TryParse(n[7..], out var i) ? i : 0)
            .DefaultIfEmpty(0).Max() ?? 0;
        return $"server-{max + 1:D3}";
    }

    // ===== 证书映射 =====

    public static List<CertificateBinding> LoadCertificates()
    {
        using var key = Registry.LocalMachine.OpenSubKey(CertificatesPath, false);
        if (key == null)
            return new List<CertificateBinding>();

        var result = new List<CertificateBinding>();
        foreach (var thumbprint in key.GetSubKeyNames())
        {
            using var sub = key.OpenSubKey(thumbprint, false);
            if (sub?.GetValue("ServerId") is not string serverId)
                continue;
            DateTime.TryParse(sub.GetValue("AddedAt") as string, null,
                System.Globalization.DateTimeStyles.RoundtripKind, out var addedAt);
            result.Add(new CertificateBinding(thumbprint, serverId, sub.GetValue("Name") as string ?? thumbprint,
                addedAt, sub.GetValue("IsTest") is int isTest && isTest != 0));
        }
        return result;
    }

    public static CertificateBinding? FindCertificate(string thumbprint) =>
        LoadCertificates().FirstOrDefault(c => c.Thumbprint == Normalize(thumbprint));

    public static void SaveCertificate(CertificateBinding binding)
    {
        using var key = Registry.LocalMachine.CreateSubKey($@"{CertificatesPath}\{Normalize(binding.Thumbprint)}", true)
            ?? throw new InvalidOperationException("无法写入注册表（需要管理员权限）");
        key.SetValue("ServerId", binding.ServerId, RegistryValueKind.String);
        key.SetValue("Name", binding.Name, RegistryValueKind.String);
        key.SetValue("AddedAt", binding.AddedAt.ToUniversalTime().ToString("o"), RegistryValueKind.String);
        key.SetValue("IsTest", binding.IsTest ? 1 : 0, RegistryValueKind.DWord);
    }

    public static void DeleteCertificate(string thumbprint)
    {
        Registry.LocalMachine.DeleteSubKeyTree($@"{CertificatesPath}\{Normalize(thumbprint)}", false);
    }

    // ===== 旧版单服务端配置 =====

    /// <summary>
    /// 旧版把配置直接放在 RemoteKSP 根键下。首次启动时迁移为 server-001；
    /// 根键下的旧值保留，旧版 KSP DLL 在升级前仍能工作
    /// </summary>
    public static bool MigrateLegacyConfig()
    {
        using var root = Registry.LocalMachine.OpenSubKey(RootPath, false);
        if (root == null || root.GetValue("ServerUrl") is not string url || root.GetValue("ClientId") is not string clientId)
            return false;

        using (var servers = Registry.LocalMachine.OpenSubKey(ServersPath, false))
        {
            if (servers != null && servers.SubKeyCount > 0)
                return false;
        }

        var secret = Unprotect(root.GetValue("SecretProtected") as byte[]);
        if (secret == null)
            return false;

        var host = Uri.TryCreate(url, UriKind.Absolute, out var uri) ? uri.Host : url;
        SaveServer(new ServerEntry("server-001", url, host, clientId, secret, null));

        // 先建 Certificates 键：Native KSP 看到它存在就不再走旧版逻辑
        using (Registry.LocalMachine.CreateSubKey(CertificatesPath, true)) { }
        if (root.GetValue("Thumbprint") is string thumbprint && !string.IsNullOrEmpty(thumbprint))
            SaveCertificate(new CertificateBinding(thumbprint, "server-001", "签名证书", DateTime.UtcNow, false));
        return true;
    }

    /// <summary>移除服务端时，如果它就是旧版配置迁移来的，一并清掉根键下的旧值</summary>
    public static void ClearLegacyConfig(string clientId)
    {
        using var root = Registry.LocalMachine.OpenSubKey(RootPath, true);
        if (root?.GetValue("ClientId") is not string legacyId || legacyId != clientId)
            return;
        foreach (var name in new[] { "ServerUrl", "ClientId", "SecretProtected", "Thumbprint" })
            root.DeleteValue(name, false);
    }

    private static string Normalize(string thumbprint) => Shared.Protocol.NormalizeThumbprint(thumbprint);

    private static byte[] Protect(string secret) =>
        ProtectedData.Protect(Encoding.UTF8.GetBytes(secret), null, DataProtectionScope.LocalMachine);

    private static string? Unprotect(byte[]? encrypted)
    {
        if (encrypted == null)
            return null;
        try
        {
            return Encoding.UTF8.GetString(ProtectedData.Unprotect(encrypted, null, DataProtectionScope.LocalMachine));
        }
        catch
        {
            return null;
        }
    }
}
