using System;
using System.IO;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

namespace RemoteKSP.Server.Core;

/// <summary>测试证书生成器（GUI 本地调用）</summary>
public static class TestCertGenerator
{
    public static (string pfxPath, string password, string thumbprint) Generate()
    {
        ServerPaths.EnsureDataDirectory();

        using var rsa = RSA.Create(2048);
        var request = new CertificateRequest(
            "CN=RemoteKSP Test Certificate - NOT FOR RELEASE",
            rsa,
            HashAlgorithmName.SHA256,
            RSASignaturePadding.Pkcs1);

        request.CertificateExtensions.Add(
            new X509BasicConstraintsExtension(false, false, 0, false));

        request.CertificateExtensions.Add(
            new X509KeyUsageExtension(X509KeyUsageFlags.DigitalSignature, false));

        // Code Signing EKU
        request.CertificateExtensions.Add(
            new X509EnhancedKeyUsageExtension(
                new OidCollection { new Oid("1.3.6.1.5.5.7.3.3") }, false));

        var cert = request.CreateSelfSigned(
            DateTimeOffset.Now.AddDays(-1),
            DateTimeOffset.Now.AddYears(2));

        var password = GenerateRandomPassword();
        // 文件名带时间戳：多张 PFX 证书可以同时登记，不能互相覆盖
        var pfxPath = Path.Combine(ServerPaths.DataDir, $"test-cert-{DateTime.Now:yyyyMMdd-HHmmss}.pfx");

        var pfxBytes = cert.Export(X509ContentType.Pfx, password);
        File.WriteAllBytes(pfxPath, pfxBytes);

        return (pfxPath, password, cert.Thumbprint.ToLowerInvariant());
    }

    private static string GenerateRandomPassword()
    {
        const string chars = "ABCDEFGHJKLMNPQRSTUVWXYZabcdefghjkmnpqrstuvwxyz23456789";
        var random = new byte[12];
        RandomNumberGenerator.Fill(random);

        var password = new char[12];
        for (int i = 0; i < 12; i++)
            password[i] = chars[random[i] % chars.Length];

        return new string(password);
    }
}
