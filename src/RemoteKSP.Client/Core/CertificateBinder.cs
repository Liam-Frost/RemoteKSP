using System;
using System.Runtime.InteropServices;
using System.Security.Cryptography.X509Certificates;

namespace RemoteKSP.Client.Core;

/// <summary>存储里已有同指纹的证书，但它的私钥不归 Remote KSP 管（例如客户端和服务端装在同一台机器上）</summary>
public class CertificateNotManagedException(string message) : InvalidOperationException(message);

/// <summary>
/// 将服务器证书绑定到 Remote KSP。只改动、删除私钥指向 Remote KSP 的证书：
/// 存储里原有的证书（服务端 eToken 证书、带真实私钥的证书）一律不碰
/// </summary>
public static class CertificateBinder
{
    public const string ProviderName = Shared.KeyProvider.RemoteKspName;

    private const uint CERT_KEY_PROV_INFO_PROP_ID = 2;
    private const uint CRYPT_MACHINE_KEYSET = 0x20;

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct CRYPT_KEY_PROV_INFO
    {
        public string pwszContainerName;
        public string pwszProvName;
        public uint dwProvType;
        public uint dwFlags;
        public uint cProvParam;
        public IntPtr rgProvParam;
        public uint dwKeySpec;
    }

    [DllImport("crypt32.dll", SetLastError = true)]
    private static extern bool CertSetCertificateContextProperty(
        IntPtr pCertContext, uint dwPropId, uint dwFlags, ref CRYPT_KEY_PROV_INFO pvData);

    /// <summary>证书私钥关联的提供程序名；没有关联（只有公钥）返回 null</summary>
    public static string? GetKeyProviderName(X509Certificate2 cert) => Shared.KeyProvider.GetName(cert);

    /// <summary>证书是不是 Remote KSP 装的（私钥指向 Remote KSP）</summary>
    public static bool IsManagedByRemoteKsp(X509Certificate2 cert) => Shared.KeyProvider.IsRemoteKsp(cert);

    /// <summary>安装证书到 LocalMachine\My，并把私钥指向 Remote KSP（容器名 = 指纹）</summary>
    public static void InstallAndBindCertificate(byte[] certDer, string thumbprint) =>
        InstallAndBindCertificate(certDer, thumbprint, StoreLocation.LocalMachine);

    /// <param name="location">测试用 CurrentUser；正式使用 LocalMachine</param>
    public static void InstallAndBindCertificate(byte[] certDer, string thumbprint, StoreLocation location)
    {
        thumbprint = thumbprint.Replace(" ", "").ToLowerInvariant();

        using var store = new X509Store(StoreName.My, location);
        store.Open(OpenFlags.ReadWrite);

        var existing = store.Certificates.Find(X509FindType.FindByThumbprint, thumbprint, false);
        foreach (var cert in existing)
        {
            if (!IsManagedByRemoteKsp(cert))
            {
                var provider = GetKeyProviderName(cert);
                throw new CertificateNotManagedException(
                    $"{location}\\My 中已有这张证书（私钥{(provider == null ? "未关联" : $"由 {provider} 提供")}），" +
                    "不是 Remote KSP 安装的，为避免破坏它已跳过。客户端和服务端装在同一台机器上时不需要同步这张证书");
            }
        }

        var added = existing.Count == 0;
        if (added)
            store.Add(X509CertificateLoader.LoadCertificate(certDer));

        // 必须在 store 里的那份证书上设置属性，内存副本上设置不会持久化
        var stored = store.Certificates.Find(X509FindType.FindByThumbprint, thumbprint, false);
        if (stored.Count == 0)
            throw new InvalidOperationException($"证书写入 {location}\\My 失败");

        // dwProvType = 0 表示 CNG KSP；KspOpenKey 用容器名（指纹）去服务端取证书
        var provInfo = new CRYPT_KEY_PROV_INFO
        {
            pwszContainerName = thumbprint,
            pwszProvName = ProviderName,
            dwProvType = 0,
            dwFlags = location == StoreLocation.LocalMachine ? CRYPT_MACHINE_KEYSET : 0,
            dwKeySpec = 0
        };

        if (!CertSetCertificateContextProperty(stored[0].Handle, CERT_KEY_PROV_INFO_PROP_ID, 0, ref provInfo))
        {
            var error = Marshal.GetLastWin32Error();
            // 刚加进去却没绑上：删掉，免得留下一张以后会被当成"不是 Remote KSP 装的"证书
            if (added)
                store.Remove(stored[0]);
            throw new InvalidOperationException($"绑定私钥到 {ProviderName} 失败 (Win32 错误 {error})");
        }
    }

    /// <summary>从 LocalMachine\My 删除 Remote KSP 装的证书；返回 false 表示有同指纹的证书不归 Remote KSP 管，已保留</summary>
    public static bool UninstallCertificate(string thumbprint) =>
        UninstallCertificate(thumbprint, StoreLocation.LocalMachine);

    /// <param name="location">测试用 CurrentUser；正式使用 LocalMachine</param>
    public static bool UninstallCertificate(string thumbprint, StoreLocation location)
    {
        using var store = new X509Store(StoreName.My, location);
        store.Open(OpenFlags.ReadWrite);

        var keptForeign = false;
        foreach (var cert in store.Certificates.Find(X509FindType.FindByThumbprint, thumbprint, false))
        {
            if (IsManagedByRemoteKsp(cert))
                store.Remove(cert);
            else
                keptForeign = true;
        }
        return !keptForeign;
    }

    /// <summary>LocalMachine\My 中指定指纹的证书；不存在返回 null</summary>
    public static X509Certificate2? GetCertificate(string thumbprint)
    {
        using var store = new X509Store(StoreName.My, StoreLocation.LocalMachine);
        store.Open(OpenFlags.ReadOnly);
        var certs = store.Certificates.Find(X509FindType.FindByThumbprint, thumbprint, false);
        return certs.Count > 0 ? certs[0] : null;
    }

    /// <summary>
    /// 查找指定指纹的证书实际位于哪个存储：LocalMachine\My、CurrentUser\My，还是两者都没有。
    /// RemoteKSP 客户端安装证书时默认装在 LocalMachine\My，但用户可能手动复制到 CurrentUser 或改绑其他证书。
    /// </summary>
    public static (StoreLocation? Location, StoreName? StoreName) FindCertificateStore(string thumbprint)
    {
        foreach (var location in new[] { StoreLocation.LocalMachine, StoreLocation.CurrentUser })
        {
            try
            {
                using var store = new X509Store(StoreName.My, location);
                store.Open(OpenFlags.ReadOnly);
                var found = store.Certificates.Find(X509FindType.FindByThumbprint, thumbprint, false);
                if (found.Count > 0)
                    return (location, StoreName.My);
            }
            catch
            {
                // 无权限或其他错误时继续检查下一个
            }
        }
        return (null, null);
    }
}
