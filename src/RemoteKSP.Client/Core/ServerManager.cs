using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using RemoteKSP.Shared;

namespace RemoteKSP.Client.Core;

/// <summary>同一指纹已经绑定到另一台服务端</summary>
public record CertificateConflict(string Thumbprint, string Name, string ExistingServerName, string NewServerName);

/// <summary>一次"更新证书"的结果</summary>
public record SyncResult(string ServerName, int Added, int Updated, int Removed, List<string> Skipped, bool LegacyServer)
{
    public string Summary
    {
        get
        {
            var text = string.Format(Strings.Sync_Summary, Added, Updated, Removed);
            if (Skipped.Count > 0)
                text += string.Format(Strings.Sync_Skipped, Skipped.Count, string.Join(Strings.List_Separator, Skipped));
            return text;
        }
    }
}

/// <summary>多服务端管理：登记、移除服务端，以及从服务端同步证书</summary>
public static class ServerManager
{
    /// <summary>配对成功后登记服务端；同一地址重新配对时沿用原来的 ID，已绑定的证书不受影响</summary>
    public static ServerEntry RegisterServer(string serverUrl, string serverName, string clientId, string secret,
        string? tlsThumbprint)
    {
        var existing = ClientRegistry.FindServerByUrl(serverUrl);
        var server = new ServerEntry(existing?.Id ?? ClientRegistry.NextServerId(), serverUrl.TrimEnd('/'),
            serverName, clientId, secret,
            string.IsNullOrEmpty(tlsThumbprint) ? null : Protocol.NormalizeThumbprint(tlsThumbprint));
        ClientRegistry.SaveServer(server);
        return server;
    }

    /// <summary>
    /// 从服务端拉取允许使用的证书并绑定到 Remote KSP；服务端撤销（或删除）的证书从本地解绑。
    /// 旧版服务端（没有 multi-cert 特性）只同步 /info 里的那一张。
    /// </summary>
    public static async Task<SyncResult> SyncCertificatesAsync(ServerEntry server,
        Func<CertificateConflict, Task<bool>> replaceExisting)
    {
        using var api = ServerApi.For(server);
        var info = await api.GetInfoAsync();

        List<RemoteCertificateInfo> remote;
        if (info.SupportsMultiCert)
            remote = await api.GetCertificatesAsync();
        else if (!string.IsNullOrEmpty(info.Thumbprint))
            remote = [new RemoteCertificateInfo(info.Thumbprint, Strings.Cert_DefaultName, null, null, info.IsTestMode)];
        else
            remote = [];

        var bindings = ClientRegistry.LoadCertificates();
        var serverNames = ClientRegistry.LoadServers().ToDictionary(s => s.Id, s => s.ServerName);
        int added = 0, updated = 0, removed = 0;
        var skipped = new List<string>();
        var remoteThumbprints = new HashSet<string>();

        foreach (var cert in remote)
        {
            var thumbprint = Protocol.NormalizeThumbprint(cert.Thumbprint);
            remoteThumbprints.Add(thumbprint);
            var existing = bindings.FirstOrDefault(b => b.Thumbprint == thumbprint);

            // 一个指纹只能绑定一台服务端：已被另一台（仍存在的）服务端绑定时让用户决定
            if (existing != null && existing.ServerId != server.Id &&
                serverNames.TryGetValue(existing.ServerId, out var otherName) &&
                !await replaceExisting(new CertificateConflict(thumbprint, cert.Name, otherName, server.ServerName)))
            {
                skipped.Add(cert.Name);
                continue;
            }

            var der = await api.DownloadCertificateAsync(thumbprint);
            try
            {
                CertificateBinder.InstallAndBindCertificate(der, thumbprint);
            }
            catch (CertificateNotManagedException)
            {
                // 本机存储里已有这张证书且不归 Remote KSP 管（例如本机就是服务端）：不覆盖、不登记
                skipped.Add(string.Format(Strings.Sync_AlreadyLocal, cert.Name));
                continue;
            }
            ClientRegistry.SaveCertificate(new CertificateBinding(thumbprint, server.Id, cert.Name,
                existing?.AddedAt ?? DateTime.UtcNow, cert.IsTest));

            if (existing?.ServerId == server.Id)
                updated++;
            else
                added++;
        }

        foreach (var binding in bindings.Where(b => b.ServerId == server.Id && !remoteThumbprints.Contains(b.Thumbprint)))
        {
            UnbindCertificate(binding.Thumbprint);
            removed++;
        }

        return new SyncResult(server.ServerName, added, updated, removed, skipped, !info.SupportsMultiCert);
    }

    /// <summary>
    /// 向各服务端查询已停用（PIN 错误或 eToken 已锁）的证书：(服务端 ID, 指纹) → 停用原因。
    /// 离线、旧版或超时的服务端跳过，不影响本地列表
    /// </summary>
    public static async Task<Dictionary<(string ServerId, string Thumbprint), string>> GetDisabledCertificatesAsync(
        IEnumerable<ServerEntry> servers, TimeSpan timeout)
    {
        async Task<List<(string, string, string)>> Query(ServerEntry server)
        {
            try
            {
                using var api = ServerApi.For(server);
                var certs = await api.GetCertificatesAsync().WaitAsync(timeout);
                return certs.Where(c => c.DisabledReason != null)
                    .Select(c => (server.Id, Protocol.NormalizeThumbprint(c.Thumbprint), c.DisabledReason!))
                    .ToList();
            }
            catch
            {
                return [];
            }
        }

        var results = await Task.WhenAll(servers.Select(Query));
        return results.SelectMany(r => r).ToDictionary(r => (r.Item1, r.Item2), r => r.Item3);
    }

    /// <summary>从 LocalMachine\My 删除 Remote KSP 装的证书并移除映射；不归 Remote KSP 管的同指纹证书保留</summary>
    public static void UnbindCertificate(string thumbprint)
    {
        CertificateBinder.UninstallCertificate(thumbprint);
        ClientRegistry.DeleteCertificate(thumbprint);
    }

    /// <summary>移除服务端及其名下所有证书，尝试通知服务端</summary>
    public static async Task<(int unboundCount, bool notified)> RemoveServerAsync(ServerEntry server)
    {
        // 1. 尝试通知服务端（超时5秒）
        var notified = await TryNotifyServerUnpairAsync(server);

        // 2. 解绑该服务端的所有证书
        var bindings = ClientRegistry.LoadCertificates().Where(b => b.ServerId == server.Id).ToList();
        foreach (var binding in bindings)
            UnbindCertificate(binding.Thumbprint);

        // 3. 删除服务端配对记录
        ClientRegistry.DeleteServer(server.Id);
        ClientRegistry.ClearLegacyConfig(server.ClientId);

        return (bindings.Count, notified);
    }

    /// <summary>尝试通知服务端客户端已解除配对（异步，超时5秒）</summary>
    private static async Task<bool> TryNotifyServerUnpairAsync(ServerEntry server)
    {
        try
        {
            using var api = new ServerApi(server.ServerUrl, server.ClientId, server.Secret, server.TlsThumbprint);
            var sendTask = api.SendAsync(System.Net.Http.HttpMethod.Post, "/unpair");
            var timeoutTask = Task.Delay(5000);

            var completed = await Task.WhenAny(sendTask, timeoutTask);
            if (completed == sendTask)
            {
                using var response = await sendTask;
                return response.IsSuccessStatusCode;
            }
            return false;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>移除服务端及其名下所有证书（同步版本，已废弃）</summary>
    [Obsolete("Use RemoveServerAsync instead")]
    public static int RemoveServer(ServerEntry server)
    {
        var bindings = ClientRegistry.LoadCertificates().Where(b => b.ServerId == server.Id).ToList();
        foreach (var binding in bindings)
            UnbindCertificate(binding.Thumbprint);

        ClientRegistry.DeleteServer(server.Id);
        ClientRegistry.ClearLegacyConfig(server.ClientId);
        return bindings.Count;
    }
}
