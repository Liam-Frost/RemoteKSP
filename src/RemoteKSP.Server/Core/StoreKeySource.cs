using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

namespace RemoteKSP.Server.Core;

/// <summary>从 Windows 证书存储读取的密钥源</summary>
public class StoreKeySource : IKeySource
{
    private readonly StoreLocation _storeLocation;
    private readonly string _thumbprint;

    public StoreKeySource(StoreLocation storeLocation, string thumbprint)
    {
        _storeLocation = storeLocation;
        _thumbprint = thumbprint;
    }

    /// <summary>签名哈希</summary>
    public byte[] SignHash(byte[] hash, string hashAlgorithm)
    {
        using var cert = LoadCertificate();
        using var rsa = cert.GetRSAPrivateKey()
            ?? throw new InvalidOperationException($"证书 {_thumbprint} 没有 RSA 私钥");

        var hashAlgName = hashAlgorithm.ToUpperInvariant() switch
        {
            "SHA256" => HashAlgorithmName.SHA256,
            "SHA1" => HashAlgorithmName.SHA1,
            "SHA384" => HashAlgorithmName.SHA384,
            "SHA512" => HashAlgorithmName.SHA512,
            _ => throw new ArgumentException($"不支持的哈希算法: {hashAlgorithm}")
        };

        return rsa.SignHash(hash, hashAlgName, RSASignaturePadding.Pkcs1);
    }

    /// <summary>获取证书公钥（DER 编码）</summary>
    public byte[] GetCertificate()
    {
        using var cert = LoadCertificate();
        return cert.Export(X509ContentType.Cert);
    }

    private X509Certificate2 LoadCertificate()
    {
        using var store = new X509Store(StoreName.My, _storeLocation);
        store.Open(OpenFlags.ReadOnly);

        var certs = store.Certificates.Find(X509FindType.FindByThumbprint, _thumbprint, false);
        if (certs.Count == 0)
        {
            var locationName = _storeLocation == StoreLocation.CurrentUser ? "CurrentUser" : "LocalMachine";
            throw new InvalidOperationException($"在 {locationName}\\My 中未找到指纹为 {_thumbprint} 的证书");
        }

        var cert = certs[0];
        if (!cert.HasPrivateKey)
            throw new InvalidOperationException($"证书 {_thumbprint} 没有私钥");
        // HasPrivateKey 只看有没有私钥关联：客户端绑定到 Remote KSP 的证书也是 true，但私钥在别的服务端上
        if (RemoteKSP.Shared.KeyProvider.IsRemoteKsp(cert))
            throw new InvalidOperationException(
                $"证书 {_thumbprint} 的私钥指向 Remote KSP（本机客户端从其他服务端同步来的），不能作为本服务端的签名证书。" +
                "eToken 证书请改用「eToken」来源");

        return cert;
    }

    /// <summary>能作为「证书存储」来源的证书：有私钥，且私钥不是指向 Remote KSP</summary>
    public static bool IsUsable(X509Certificate2 cert) => cert.HasPrivateKey && !RemoteKSP.Shared.KeyProvider.IsRemoteKsp(cert);
}
