using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;

namespace RemoteKSP.Server.Core;

public class ClientStore
{
    private readonly string _filePath;
    private readonly object _lock = new();
    private List<PairedClient> _clients = new();

    public ClientStore(string dataDir)
    {
        _filePath = Path.Combine(dataDir, "clients.json");
        Load();
    }

    public void Add(PairedClient client)
    {
        lock (_lock)
        {
            _clients.Add(client);
            Save();
        }
    }

    public PairedClient? GetById(string clientId)
    {
        lock (_lock)
        {
            return _clients.FirstOrDefault(c => c.Id == clientId && !c.IsRevoked);
        }
    }

    public List<PairedClient> GetAll()
    {
        lock (_lock)
        {
            return new List<PairedClient>(_clients.Where(c => !c.IsRevoked));
        }
    }

    public void UpdateLastSeen(string clientId)
    {
        lock (_lock)
        {
            var client = _clients.FirstOrDefault(c => c.Id == clientId);
            if (client != null)
            {
                var now = DateTime.UtcNow;
                // Throttle writes: only update if > 1 minute since last
                if ((now - client.LastSeen).TotalMinutes > 1)
                {
                    client.LastSeen = now;
                    Save();
                }
            }
        }
    }

    public void Revoke(string clientId)
    {
        lock (_lock)
        {
            var client = _clients.FirstOrDefault(c => c.Id == clientId);
            if (client != null)
            {
                client.IsRevoked = true;
                Save();
            }
        }
    }

    public void SetAllowedCertIds(string clientId, List<string>? allowedCertIds)
    {
        lock (_lock)
        {
            var client = _clients.FirstOrDefault(c => c.Id == clientId && !c.IsRevoked)
                ?? throw new InvalidOperationException(Strings.Client_NotFound);
            client.AllowedCertIds = allowedCertIds;
            Save();
        }
    }

    public void RevokeByMachineName(string machineName)
    {
        lock (_lock)
        {
            var clients = _clients.Where(c => c.MachineName == machineName && !c.IsRevoked).ToList();
            foreach (var client in clients)
            {
                client.IsRevoked = true;
            }
            if (clients.Any())
            {
                Save();
            }
        }
    }

    /// <summary>非 null 表示 clients.json 解析失败：原文件已改名保留，当前没有已配对的客户端</summary>
    public string? LoadError { get; private set; }

    private bool _saveBlocked;

    public void Save()
    {
        lock (_lock)
        {
            if (_saveBlocked)
                throw new InvalidOperationException(LoadError);
            var json = JsonSerializer.Serialize(_clients, new JsonSerializerOptions { WriteIndented = true });
            SecureStorage.WriteAllTextAtomic(_filePath, json);
        }
    }

    private void Load()
    {
        lock (_lock)
        {
            if (!File.Exists(_filePath))
                return;
            try
            {
                var json = SecureStorage.ReadAllText(_filePath);
                _clients = JsonSerializer.Deserialize<List<PairedClient>>(json)
                    ?? throw new JsonException(Strings.Store_FileIsNull);
            }
            catch (Exception ex) when (ex is JsonException or NotSupportedException)
            {
                // 不能拿空列表覆盖：原文件改名保留，管理员可以从里面恢复；改名也失败就禁止写入
                _clients = new List<PairedClient>();
                string kept;
                try
                {
                    kept = string.Format(Strings.Store_CorruptKept, Path.GetFileName(SecureStorage.PreserveCorruptFile(_filePath)));
                }
                catch (Exception moveEx) when (moveEx is IOException or UnauthorizedAccessException)
                {
                    _saveBlocked = true;
                    kept = string.Format(Strings.Store_CorruptKeepFailed, moveEx.Message);
                }
                LoadError = string.Format(Strings.Client_StoreCorrupt, ex.Message, kept);
            }
        }
    }
}

public class PairedClient
{
    public string Id { get; set; } = "";
    public string MachineName { get; set; } = "";
    public string IpAddress { get; set; } = "";
    public string SecretEncrypted { get; set; } = "";
    public DateTime PairedAt { get; set; }
    public DateTime LastSeen { get; set; }
    public bool IsRevoked { get; set; }

    /// <summary>null = 允许所有分享的证书；[] = 全部禁止；否则仅允许列出的证书 ID</summary>
    public List<string>? AllowedCertIds { get; set; }

    /// <summary>该客户端能否使用这张证书：证书必须已分享，且在客户端的允许列表里</summary>
    public bool CanUse(CertificateEntry cert) =>
        cert.IsShared && (AllowedCertIds == null || AllowedCertIds.Contains(cert.Id));
}
