using System;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

namespace RemoteKSP.Server.Core;

/// <summary>
/// 测试证书：只有公钥，用来验证配对和证书下发。签名请求一律抛 <see cref="TestModeException"/>。
/// </summary>
public class TestKeySource : IKeySource
{
    private readonly byte[] _certDer;

    public TestKeySource(byte[] certDer)
    {
        _certDer = certDer;
    }

    public byte[] GetCertificate() => _certDer;

    public byte[] SignHash(byte[] hash, string hashAlgorithm) => throw new TestModeException();

    /// <summary>生成一张新的虚拟代码签名证书，返回 DER（私钥随即丢弃）</summary>
    public static byte[] GenerateCertificateDer(string subjectName)
    {
        using var rsa = RSA.Create(2048);
        var request = new CertificateRequest($"CN={subjectName}", rsa,
            HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);

        request.CertificateExtensions.Add(new X509BasicConstraintsExtension(false, false, 0, false));
        request.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.DigitalSignature, false));
        request.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension(
            new OidCollection { new Oid("1.3.6.1.5.5.7.3.3") }, false)); // Code Signing

        using var cert = request.CreateSelfSigned(DateTimeOffset.Now.AddDays(-1), DateTimeOffset.Now.AddYears(5));
        return cert.Export(X509ContentType.Cert);
    }
}

public class TestModeException : Exception
{
    public TestModeException() : base(Strings.Test_SigningRefused) { }
}
