using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

namespace RemoteKSP.Server.Core;

/// <summary>测试用 PFX 密钥源</summary>
public class PfxKeySource : IKeySource
{
    private readonly string _pfxPath;
    private readonly string _password;
    private readonly string _thumbprint;

    public PfxKeySource(string pfxPath, string password, string thumbprint)
    {
        _pfxPath = pfxPath;
        _password = password;
        _thumbprint = thumbprint;
    }

    /// <summary>签名哈希</summary>
    public byte[] SignHash(byte[] hash, string hashAlgorithm)
    {
        using var cert = X509CertificateLoader.LoadPkcs12FromFile(_pfxPath, _password,
            X509KeyStorageFlags.EphemeralKeySet);

        if (cert.GetCertHashString().Replace(" ", "").ToLower() != _thumbprint)
            throw new InvalidOperationException("PFX thumbprint mismatch");

        using var rsa = cert.GetRSAPrivateKey()
            ?? throw new InvalidOperationException("No RSA private key");

        var hashAlgName = hashAlgorithm.ToUpperInvariant() switch
        {
            "SHA256" => HashAlgorithmName.SHA256,
            "SHA1" => HashAlgorithmName.SHA1,
            "SHA384" => HashAlgorithmName.SHA384,
            "SHA512" => HashAlgorithmName.SHA512,
            _ => throw new ArgumentException($"Unsupported hash algorithm: {hashAlgorithm}")
        };

        return rsa.SignHash(hash, hashAlgName, RSASignaturePadding.Pkcs1);
    }

    /// <summary>获取证书公钥（DER 编码）</summary>
    public byte[] GetCertificate()
    {
        using var cert = X509CertificateLoader.LoadPkcs12FromFile(_pfxPath, _password,
            X509KeyStorageFlags.EphemeralKeySet);
        return cert.Export(X509ContentType.Cert);
    }
}
