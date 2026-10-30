using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace RemoteKSP.Server.Core;

/// <summary>服务端配置</summary>
public class ServerConfig
{
    /// <summary>HTTPS 监听端口</summary>
    public int Port { get; set; } = 8443;

    /// <summary>密钥源类型：etoken, pfx, store</summary>
    public string KeySourceType { get; set; } = "etoken";

    /// <summary>证书存储位置：CurrentUser 或 LocalMachine（仅 store 类型使用）</summary>
    public string? StoreLocation { get; set; } = "LocalMachine";

    /// <summary>eToken CSP 提供程序名称</summary>
    public string? CspProvider { get; set; } = "eToken Base Cryptographic Provider";

    /// <summary>eToken 密钥容器名</summary>
    public string? KeyContainer { get; set; }

    /// <summary>证书指纹（SHA1，小写无分隔符）</summary>
    public string? Thumbprint { get; set; }

    /// <summary>测试用 PFX 文件路径（相对于数据目录）</summary>
    public string? TestPfxFile { get; set; }

    /// <summary>测试 PFX 密码（DPAPI 加密的 base64）</summary>
    public string? TestPfxPassword { get; set; }

    /// <summary>TLS 证书指纹</summary>
    public string? TlsCertThumbprint { get; set; }

    [JsonIgnore]
    public bool IsConfigured => !string.IsNullOrEmpty(Thumbprint) &&
        (KeySourceType == "pfx" || KeySourceType == "store" || !string.IsNullOrEmpty(KeyContainer));

    public static ServerConfig Load()
    {
        if (!File.Exists(ServerPaths.ConfigFile))
            return new ServerConfig();

        try
        {
            var json = SecureStorage.ReadAllText(ServerPaths.ConfigFile);
            return JsonSerializer.Deserialize<ServerConfig>(json) ?? new ServerConfig();
        }
        catch (JsonException)
        {
            // 下一次保存会写入默认值：先把损坏的文件改名保留（里面有 TLS 证书指纹等）；改名失败就不允许保存
            try
            {
                SecureStorage.PreserveCorruptFile(ServerPaths.ConfigFile);
                return new ServerConfig();
            }
            catch
            {
                return new ServerConfig { _readFailed = true };
            }
        }
        catch
        {
            // 读取失败（文件被占用等）：返回默认值供显示用，但不能拿它覆盖真实的 config.json
            return new ServerConfig { _readFailed = true };
        }
    }

    /// <summary>config.json 存在但没读出来：这份是默认值，保存会覆盖真实配置</summary>
    private bool _readFailed;

    public void Save()
    {
        if (_readFailed)
            throw new InvalidOperationException($"{ServerPaths.ConfigFile} 读取失败，为避免用默认值覆盖它，未保存配置");
        ServerPaths.EnsureDataDirectory();
        var json = JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true });
        SecureStorage.WriteAllTextAtomic(ServerPaths.ConfigFile, json);
    }
}
