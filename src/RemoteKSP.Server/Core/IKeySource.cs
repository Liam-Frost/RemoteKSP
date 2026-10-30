namespace RemoteKSP.Server.Core;

/// <summary>
/// 密钥源接口。每个证书对应一个密钥源实现。
/// </summary>
public interface IKeySource
{
    /// <summary>获取证书公钥（DER 编码）</summary>
    byte[] GetCertificate();

    /// <summary>签名哈希</summary>
    byte[] SignHash(byte[] hash, string hashAlgorithm);
}
