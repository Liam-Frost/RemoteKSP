using System;
using System.IO;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

namespace RemoteKSP.Server.Core;

/// <summary>TLS 证书管理</summary>
public static class TlsCertificateManager
{
    /// <summary>
    /// 确保 TLS 证书存在。客户端按配对时记录的指纹固定这张证书，换证书后所有客户端都要重新配对，所以
    /// server.pfx 存在就一直用它：不按有效期轮换（客户端和 Native KSP 都不检查有效期），读不出来时报错而不重新生成
    /// </summary>
    public static X509Certificate2 EnsureCertificate()
    {
        ServerPaths.EnsureDataDirectory();
        var cert = LoadOrCreate(ServerPaths.TlsCertFile);
        SyncConfigThumbprint(cert);
        _current = cert;
        return cert;
    }

    /// <summary>HTTPS 监听当前使用的证书。Kestrel 每次握手读取它，重新生成后新连接立即用新证书，不用重启服务</summary>
    public static X509Certificate2? Current => _current;

    private static volatile X509Certificate2? _current;
    private static readonly object RegenerateLock = new();

    /// <summary>config.json 丢失或损坏时以 server.pfx 为准：配对验证码要用客户端实际看到的这张证书的指纹</summary>
    private static void SyncConfigThumbprint(X509Certificate2 cert)
    {
        var config = ServerConfig.Load();
        var thumbprint = cert.Thumbprint.ToLowerInvariant();
        if (!string.Equals(config.TlsCertThumbprint, thumbprint, StringComparison.OrdinalIgnoreCase))
        {
            config.TlsCertThumbprint = thumbprint;
            config.Save();
        }
    }

    /// <param name="OldThumbprint">换掉的证书指纹；原来没有 server.pfx 或读不出来时为 null</param>
    /// <param name="BackupFile">原 server.pfx 的备份文件名（数据目录内）；原来没有文件时为 null</param>
    public record RegenerateResult(string? OldThumbprint, string NewThumbprint, string? BackupFile);

    /// <summary>
    /// 重新生成服务端 TLS 证书并立即生效。已配对的客户端按旧指纹固定证书，之后都要移除服务端并重新配对。
    /// 原 server.pfx 复制备份（不删除），想恢复时停止服务、把备份改回 server.pfx 再启动。
    /// 服务运行时由服务调用（新连接立即用新证书）；服务因证书损坏起不来时由管理员 GUI 直接调用
    /// </summary>
    public static RegenerateResult RegenerateServerCertificate()
    {
        lock (RegenerateLock)
        {
            ServerPaths.EnsureDataDirectory();
            var result = Regenerate(ServerPaths.TlsCertFile, out var cert);
            SyncConfigThumbprint(cert);
            _current = cert;
            return result;
        }
    }

    /// <summary>生成新证书写入 <paramref name="pfxPath"/>；已有文件先复制一份备份</summary>
    public static RegenerateResult Regenerate(string pfxPath, out X509Certificate2 cert)
    {
        string? oldThumbprint = null, backup = null;
        if (File.Exists(pfxPath))
        {
            // 读不出来（这正是需要重新生成的典型原因）也照样备份原文件
            try
            {
                using var old = X509CertificateLoader.LoadPkcs12(DpapiHelper.Unprotect(File.ReadAllBytes(pfxPath)), null,
                    X509KeyStorageFlags.EphemeralKeySet);
                oldThumbprint = old.Thumbprint.ToLowerInvariant();
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or CryptographicException) { }

            backup = $"{pfxPath}.bak-{DateTime.Now:yyyyMMdd-HHmmss-fff}";
            File.Copy(pfxPath, backup, overwrite: false);
        }

        cert = CreateAndSave(pfxPath);
        return new RegenerateResult(oldThumbprint, cert.Thumbprint.ToLowerInvariant(),
            backup == null ? null : Path.GetFileName(backup));
    }

    /// <summary>生成自签名证书，加密写入文件，再按 Kestrel/Schannel 需要的方式（持久化到机器密钥库）加载</summary>
    private static X509Certificate2 CreateAndSave(string pfxPath)
    {
        // 直接导出刚生成的内存证书：先导入机器密钥库再导出，未提权时会"密钥集不存在"
        var pfx = GenerateSelfSignedPfx();
        SecureStorage.WriteAllBytesAtomic(pfxPath, DpapiHelper.Protect(pfx));
        return X509CertificateLoader.LoadPkcs12(pfx, null,
            X509KeyStorageFlags.Exportable | X509KeyStorageFlags.PersistKeySet | X509KeyStorageFlags.MachineKeySet);
    }

    /// <summary>读取 TLS 证书；文件不存在时生成并保存。文件存在但读不出来时抛异常，不覆盖它</summary>
    public static X509Certificate2 LoadOrCreate(string pfxPath)
    {
        if (File.Exists(pfxPath))
        {
            try
            {
                return X509CertificateLoader.LoadPkcs12(DpapiHelper.Unprotect(File.ReadAllBytes(pfxPath)), null,
                    X509KeyStorageFlags.Exportable | X509KeyStorageFlags.PersistKeySet | X509KeyStorageFlags.MachineKeySet);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or CryptographicException)
            {
                throw new InvalidOperationException(string.Format(Strings.Tls_ReadFailed, pfxPath, ex.Message), ex);
            }
        }

        return CreateAndSave(pfxPath);
    }

    private static byte[] GenerateSelfSignedPfx()
    {
        using var rsa = RSA.Create(2048);
        var request = new CertificateRequest(
            "CN=RemoteKSP Server",
            rsa,
            HashAlgorithmName.SHA256,
            RSASignaturePadding.Pkcs1);

        request.CertificateExtensions.Add(
            new X509BasicConstraintsExtension(false, false, 0, false));

        request.CertificateExtensions.Add(
            new X509KeyUsageExtension(
                X509KeyUsageFlags.DigitalSignature | X509KeyUsageFlags.KeyEncipherment,
                false));

        request.CertificateExtensions.Add(
            new X509EnhancedKeyUsageExtension(
                new OidCollection { new Oid("1.3.6.1.5.5.7.3.1") }, // serverAuth
                false));

        using var cert = request.CreateSelfSigned(
            DateTimeOffset.Now.AddDays(-1),
            DateTimeOffset.Now.AddYears(5));
        return cert.Export(X509ContentType.Pfx);
    }

    public static X509Certificate2? LoadCertificate()
    {
        var config = ServerConfig.Load();
        if (string.IsNullOrEmpty(config.TlsCertThumbprint))
            return null;

        return LoadCertificate(config.TlsCertThumbprint);
    }

    /// <summary>确保 Dummy 签名证书存在（用于测试模式）</summary>
    public static X509Certificate2 EnsureDummySigningCert()
    {
        var dummyCertPath = Path.Combine(ServerPaths.DataDir, "dummy-signing-cert.pfx");

        if (File.Exists(dummyCertPath))
        {
            try
            {
                var encryptedBytes = File.ReadAllBytes(dummyCertPath);
                var decrypted = DpapiHelper.Unprotect(encryptedBytes);
                // 只用来导出公钥证书，不落地私钥，避免每次启动在机器密钥目录留下文件
                return X509CertificateLoader.LoadPkcs12(decrypted, "dummy", X509KeyStorageFlags.EphemeralKeySet);
            }
            catch
            {
                // 损坏则重新生成
            }
        }

        // 生成新的 Dummy 证书
        var (cert, pfxBytes) = GenerateDummyCodeSigningCert();
        SaveDummyCert(pfxBytes, dummyCertPath);
        return cert;
    }

    private static (X509Certificate2 cert, byte[] pfxBytes) GenerateDummyCodeSigningCert()
    {
        using var rsa = RSA.Create(2048);
        var request = new CertificateRequest(
            "CN=RemoteKSP Dummy Test Certificate",
            rsa,
            HashAlgorithmName.SHA256,
            RSASignaturePadding.Pkcs1);

        request.CertificateExtensions.Add(
            new X509BasicConstraintsExtension(false, false, 0, false));

        request.CertificateExtensions.Add(
            new X509KeyUsageExtension(
                X509KeyUsageFlags.DigitalSignature,
                false));

        // Code Signing EKU
        request.CertificateExtensions.Add(
            new X509EnhancedKeyUsageExtension(
                new OidCollection { new Oid("1.3.6.1.5.5.7.3.3") },
                false));

        var cert = request.CreateSelfSigned(
            DateTimeOffset.Now.AddDays(-1),
            DateTimeOffset.Now.AddYears(5));

        // 直接保存 CreateSelfSigned 导出的 PFX，避免从重新加载的（不可导出的）证书再次导出
        var pfxBytes = cert.Export(X509ContentType.Pfx, "dummy");
        var loadedCert = X509CertificateLoader.LoadPkcs12(pfxBytes, "dummy", X509KeyStorageFlags.EphemeralKeySet);
        return (loadedCert, pfxBytes);
    }

    private static void SaveDummyCert(byte[] certData, string path)
    {
        ServerPaths.EnsureDataDirectory();
        var encrypted = DpapiHelper.Protect(certData);
        SecureStorage.WriteAllBytesAtomic(path, encrypted);
    }

    private static X509Certificate2? LoadCertificate(string thumbprint)
    {
        if (!File.Exists(ServerPaths.TlsCertFile))
            return null;

        try
        {
            var encrypted = File.ReadAllBytes(ServerPaths.TlsCertFile);
            var pfxBytes = DpapiHelper.Unprotect(encrypted);
            var cert = X509CertificateLoader.LoadPkcs12(pfxBytes, null,
                X509KeyStorageFlags.Exportable | X509KeyStorageFlags.PersistKeySet | X509KeyStorageFlags.MachineKeySet);

            if (cert.Thumbprint.Equals(thumbprint, StringComparison.OrdinalIgnoreCase))
                return cert;

            return null;
        }
        catch
        {
            return null;
        }
    }
}
