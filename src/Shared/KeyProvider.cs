using System;
using System.Runtime.InteropServices;
using System.Security.Cryptography.X509Certificates;

namespace RemoteKSP.Shared;

/// <summary>证书私钥由哪个提供程序提供（CERT_KEY_PROV_INFO_PROP_ID）。客户端和服务端共用</summary>
public static class KeyProvider
{
    /// <summary>客户端绑定证书时写入的提供程序名，也是 native KSP 注册的名字</summary>
    public const string RemoteKspName = "Remote KSP";

    private const uint CERT_KEY_PROV_INFO_PROP_ID = 2;

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
    private static extern bool CertGetCertificateContextProperty(
        IntPtr pCertContext, uint dwPropId, IntPtr pvData, ref uint pcbData);

    /// <summary>证书私钥关联的提供程序名；没有关联（只有公钥）返回 null</summary>
    public static string? GetName(X509Certificate2 cert)
    {
        uint size = 0;
        if (!CertGetCertificateContextProperty(cert.Handle, CERT_KEY_PROV_INFO_PROP_ID, IntPtr.Zero, ref size))
            return null;

        var buffer = Marshal.AllocHGlobal((int)size);
        try
        {
            if (!CertGetCertificateContextProperty(cert.Handle, CERT_KEY_PROV_INFO_PROP_ID, buffer, ref size))
                return null;
            return Marshal.PtrToStructure<CRYPT_KEY_PROV_INFO>(buffer).pwszProvName;
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    /// <summary>
    /// 私钥指向 Remote KSP：这是客户端同步下来的证书，私钥在别的服务端上。
    /// 服务端拿它签名会把请求转给那台服务端，指向本机时会绕回自己
    /// </summary>
    public static bool IsRemoteKsp(X509Certificate2 cert) =>
        string.Equals(GetName(cert), RemoteKspName, StringComparison.OrdinalIgnoreCase);
}
