using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using RemoteKSP.Shared;

namespace RemoteKSP.Server.Core;

/// <summary>
/// 证书注册表管理。负责持久化和查询服务端的所有证书条目。
/// </summary>
public class CertificateRegistry
{
    private readonly string _filePath;
    private readonly object _lock = new();
    private List<CertificateEntry> _entries = new();

    public CertificateRegistry(string dataDir)
    {
        _filePath = Path.Combine(dataDir, "certificates.json");
        Load();
    }

    /// <summary>获取所有证书条目</summary>
    public List<CertificateEntry> GetAll()
    {
        lock (_lock)
        {
            return new List<CertificateEntry>(_entries);
        }
    }

    /// <summary>按 ID 查找证书</summary>
    public CertificateEntry? GetById(string id)
    {
        lock (_lock)
        {
            return _entries.FirstOrDefault(e => e.Id == id);
        }
    }

    /// <summary>按指纹查找证书</summary>
    public CertificateEntry? GetByThumbprint(string thumbprint)
    {
        var normalized = Protocol.NormalizeThumbprint(thumbprint);
        lock (_lock)
        {
            return _entries.FirstOrDefault(e =>
                Protocol.NormalizeThumbprint(e.Thumbprint) == normalized);
        }
    }

    /// <summary>获取所有分享的证书（客户端可见）</summary>
    public List<CertificateEntry> GetShared()
    {
        lock (_lock)
        {
            return _entries.Where(e => e.IsShared).ToList();
        }
    }

    /// <summary>添加证书；同一指纹只能登记一次</summary>
    public void Add(CertificateEntry entry)
    {
        lock (_lock)
        {
            entry.Thumbprint = Protocol.NormalizeThumbprint(entry.Thumbprint);
            var existing = _entries.FirstOrDefault(e => e.Thumbprint == entry.Thumbprint);
            if (existing != null)
                throw new InvalidOperationException($"该证书已登记为「{existing.Name}」");

            entry.Id = NextId();
            entry.AddedAt = DateTime.UtcNow;
            _entries.Add(entry);
            Save();
        }
    }

    /// <summary>更新 eToken PIN（DPAPI 加密后保存），同时解除 PIN 停用</summary>
    public void SetPin(string id, string pin)
    {
        lock (_lock)
        {
            var entry = _entries.FirstOrDefault(e => e.Id == id)
                ?? throw new InvalidOperationException("证书不存在");
            entry.PinEncrypted = DpapiHelper.ProtectString(pin);
            entry.PinFailure = null;
            entry.PinFailedAt = null;
            Save();
        }
    }

    /// <summary>token 拒绝了保存的 PIN：删除 PIN 并记下停用原因，服务重启后也不会再用它访问 token</summary>
    public void MarkPinFailure(string id, PinFailure failure)
    {
        lock (_lock)
        {
            var entry = _entries.FirstOrDefault(e => e.Id == id);
            if (entry == null)
                return;
            entry.PinEncrypted = null;
            entry.PinFailure = failure.Kind;
            entry.PinFailedAt = failure.OccurredAt;
            Save();
        }
    }

    private string NextId()
    {
        int maxNum = 0;
        foreach (var e in _entries)
        {
            if (e.Id.StartsWith("cert-") && int.TryParse(e.Id.Substring(5), out int num))
                maxNum = Math.Max(maxNum, num);
        }
        return $"cert-{maxNum + 1:D3}";
    }

    /// <summary>更新证书（仅允许修改 Name 和 IsShared）</summary>
    public void Update(string id, string name, bool isShared)
    {
        lock (_lock)
        {
            var entry = _entries.FirstOrDefault(e => e.Id == id);
            if (entry != null)
            {
                entry.Name = name;
                entry.IsShared = isShared;
                Save();
            }
        }
    }

    /// <summary>删除证书</summary>
    public void Remove(string id)
    {
        lock (_lock)
        {
            _entries.RemoveAll(e => e.Id == id);
            Save();
        }
    }

    /// <summary>设置默认证书（移到列表首位，用于 /info 兼容）</summary>
    public void SetDefault(string id)
    {
        lock (_lock)
        {
            var entry = _entries.FirstOrDefault(e => e.Id == id);
            if (entry != null)
            {
                _entries.Remove(entry);
                _entries.Insert(0, entry);
                Save();
            }
        }
    }

    /// <summary>非 null 表示 certificates.json 解析失败：原文件已改名保留，当前列表为空</summary>
    public string? LoadError { get; private set; }

    private bool _saveBlocked;

    private void Load()
    {
        lock (_lock)
        {
            if (File.Exists(_filePath))
            {
                try
                {
                    var json = SecureStorage.ReadAllText(_filePath);
                    var data = JsonSerializer.Deserialize<CertificateRegistryData>(json)
                        ?? throw new JsonException("文件内容为 null");
                    _entries = data.Certificates ?? new List<CertificateEntry>();
                }
                catch (Exception ex) when (ex is JsonException or NotSupportedException)
                {
                    // 不能拿空列表覆盖：原文件改名保留，管理员可以从里面恢复；改名也失败就禁止写入
                    _entries = new List<CertificateEntry>();
                    string kept;
                    try
                    {
                        kept = $"已改名保留为 {Path.GetFileName(SecureStorage.PreserveCorruptFile(_filePath))}";
                    }
                    catch (Exception moveEx) when (moveEx is IOException or UnauthorizedAccessException)
                    {
                        _saveBlocked = true;
                        kept = $"改名保留失败（{moveEx.Message}），排除问题前不会写入该文件";
                    }
                    LoadError = $"certificates.json 无法解析（{ex.Message}），{kept}，当前证书列表为空";
                }
            }
            else
            {
                // 迁移旧配置
                MigrateFromLegacyConfig();
            }
        }
    }

    private void Save()
    {
        lock (_lock)
        {
            if (_saveBlocked)
                throw new InvalidOperationException(LoadError);
            ServerPaths.EnsureDataDirectory();
            var data = new CertificateRegistryData { Certificates = _entries };
            var json = JsonSerializer.Serialize(data, new JsonSerializerOptions { WriteIndented = true });
            SecureStorage.WriteAllTextAtomic(_filePath, json);
        }
    }

    /// <summary>从旧的 config.json 迁移到证书注册表</summary>
    private void MigrateFromLegacyConfig()
    {
        try
        {
            var config = ServerConfig.Load();
            if (config.IsConfigured)
            {
                var entry = new CertificateEntry
                {
                    Name = config.KeySourceType switch
                    {
                        "etoken" => "eToken 签名证书",
                        "pfx" => "测试 PFX 证书",
                        "store" => "证书存储签名证书",
                        _ => "迁移的签名证书"
                    },
                    Thumbprint = config.Thumbprint ?? "",
                    SourceType = config.KeySourceType ?? "etoken",
                    IsShared = true,
                    CspProvider = config.CspProvider,
                    KeyContainer = config.KeyContainer,
                    StoreLocation = config.StoreLocation,
                    PfxPath = config.TestPfxFile,
                    PfxPasswordEncrypted = config.TestPfxPassword
                };

                // PIN 从 pin.dat 读取（如果存在）
                var pin = DpapiHelper.LoadPin();
                if (pin != null && config.KeySourceType == "etoken")
                    entry.PinEncrypted = DpapiHelper.ProtectString(pin);

                entry.Id = NextId();
                _entries.Add(entry);
            }
        }
        catch
        {
            // 旧配置损坏：忽略，下面补一张测试证书
        }

        // 旧版未配置时运行在测试模式；补一张测试证书，保证新装的服务端也能直接测配对
        if (_entries.Count == 0)
        {
            try
            {
                using var dummy = TlsCertificateManager.EnsureDummySigningCert();
                _entries.Add(new CertificateEntry
                {
                    Id = NextId(),
                    Name = "测试证书",
                    Thumbprint = dummy.Thumbprint.ToLowerInvariant(),
                    SourceType = "test",
                    IsShared = true,
                    TestCertDer = Convert.ToBase64String(dummy.Export(System.Security.Cryptography.X509Certificates.X509ContentType.Cert))
                });
            }
            catch
            {
                // 生成失败则保持空列表
            }
        }

        try { Save(); } catch { /* GUI 进程可能没有写权限，交给服务写 */ }
    }

    private class CertificateRegistryData
    {
        public List<CertificateEntry> Certificates { get; set; } = new();
    }
}
