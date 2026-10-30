using System;
using System.Collections.Concurrent;
using System.Linq;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using Microsoft.Extensions.Logging;

namespace RemoteKSP.Server.Core;

/// <summary>
/// eToken 密钥源。token 拒绝 PIN（错误或已锁）时立即清掉缓存的 PIN 并停用，
/// 之后的签名请求直接失败、不再访问 token，一个错误 PIN 最多只消耗 token 一次重试次数。
/// 签名失败只有在确定还没校验 PIN 时（没插卡、卡忙、超时）才允许下次再试；
/// token 插着、错误码又不认识时同样停用，不拿同一个 PIN 反复去试。
/// </summary>
public class ETokenKeySource : IKeySource
{
    private readonly string _cspProvider;
    private readonly string _keyContainer;
    private readonly string _thumbprint;
    private readonly ILogger<ETokenKeySource> _logger;

    /// <summary>
    /// 同一个 token 容器在进程内共用的签名锁和"刚被拒绝的 PIN"。GUI 改动任何证书都会 Reload、新建密钥源对象，
    /// 正在签名的旧对象和新对象若各用一把锁，同一个错误 PIN 会被新旧对象各试一次
    /// </summary>
    private sealed class ContainerGuard
    {
        public readonly object SignLock = new();
        /// <summary>token 拒绝过的 PIN 的 SHA-256；同一个 PIN 不再拿去试 token</summary>
        public byte[]? RejectedPinHash;
    }

    private static readonly ConcurrentDictionary<string, ContainerGuard> Guards = new(StringComparer.OrdinalIgnoreCase);

    // 签名串行化：并发请求不会各自拿着同一个错误 PIN 去试 token
    private readonly ContainerGuard _guard;
    private volatile string? _cachedPin;
    private volatile PinFailure? _failure;

    public bool HasPin => _cachedPin != null;

    /// <summary>非 null 表示证书已因 PIN 被拒停用</summary>
    public PinFailure? Failure => _failure;

    private static byte[] PinHash(string pin) => SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(pin));

    private bool IsKnownRejected(string pin) =>
        _guard.RejectedPinHash is { } rejected && CryptographicOperations.FixedTimeEquals(rejected, PinHash(pin));

    public ETokenKeySource(string cspProvider, string keyContainer, string thumbprint,
        ILogger<ETokenKeySource> logger)
    {
        _cspProvider = cspProvider;
        _keyContainer = keyContainer;
        _thumbprint = thumbprint;
        _logger = logger;
        _guard = Guards.GetOrAdd(PinAttemptLimiter.KeyFor(cspProvider, keyContainer), _ => new ContainerGuard());
    }

    /// <summary>设置 PIN（缓存在内存中）</summary>
    public void SetPin(string pin) => _cachedPin = pin;

    /// <summary>恢复持久化的停用状态（服务重启后不会再拿错误 PIN 去试 token）</summary>
    public void RestoreFailure(PinFailure failure)
    {
        _cachedPin = null;
        _failure = failure;
    }

    /// <summary>验证 PIN（通过测试签名）；成功后缓存 PIN 并解除停用</summary>
    public PinVerifyResult VerifyPin(string pin)
    {
        var testHash = new byte[32];
        RandomNumberGenerator.Fill(testHash);
        lock (_guard.SignLock)
        {
            try
            {
                _ = SignWithPin(pin, testHash, HashAlgorithmName.SHA256);
                _cachedPin = pin;
                _failure = null;
                _guard.RejectedPinHash = null; // 登录成功后 token 重试计数已复位
                return new PinVerifyResult(true, null, null);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "PIN verification failed");
                var kind = PinFailure.Classify(ex);
                if (kind == PinFailureKind.Wrong)
                    _guard.RejectedPinHash = PinHash(pin);
                return new PinVerifyResult(false, kind, ex.Message);
            }
        }
    }

    /// <summary>签名哈希；PIN 被拒抛 <see cref="PinRejectedException"/></summary>
    public byte[] SignHash(byte[] hash, string hashAlgorithm)
    {
        var hashAlgName = hashAlgorithm.ToUpperInvariant() switch
        {
            "SHA256" => HashAlgorithmName.SHA256,
            "SHA1" => HashAlgorithmName.SHA1,
            "SHA384" => HashAlgorithmName.SHA384,
            "SHA512" => HashAlgorithmName.SHA512,
            _ => throw new ArgumentException($"Unsupported hash algorithm: {hashAlgorithm}")
        };

        lock (_guard.SignLock)
        {
            if (_failure is { } existing)
                throw new PinRejectedException(existing, justDisabled: false);

            var pin = _cachedPin ?? throw new InvalidOperationException("PIN not set");

            // 另一个对象（例如 Reload 前的旧对象）刚被 token 拒绝过同一个 PIN：直接停用，不再访问 token
            if (IsKnownRejected(pin))
                throw Disable(PinFailureKind.Wrong, null);

            try
            {
                var signature = SignWithPin(pin, hash, hashAlgName);
                _tokenPresent = true;
                return signature;
            }
            catch (Exception ex) when (PinFailure.Classify(ex) is { } kind)
            {
                if (kind == PinFailureKind.Wrong)
                    _guard.RejectedPinHash = PinHash(pin);
                _logger.LogError(ex, "eToken rejected PIN ({Kind}); certificate disabled", kind);
                throw Disable(kind, ex);
            }
            catch (Exception ex) when (ex is not ArgumentException)
            {
                // 不是已知的 PIN 错误。没插卡时 CSP 碰不到卡，不可能校验 PIN，可以等下一次请求再试；
                // 卡插着却失败、错误码又不认识，可能是 CSP 用别的错误码表示 PIN 错误：停用，不再拿这个 PIN 去试
                if (PinFailure.IsBeforePinCheck(ex) || !(_tokenPresent = IsTokenPresent()))
                {
                    _logger.LogWarning(ex, "eToken signing failed before PIN verification (token present: {Present}); will retry on the next request",
                        _tokenPresent);
                    throw new InvalidOperationException(
                        _tokenPresent ? string.Format(Strings.EToken_Unavailable, ex.Message) : Strings.EToken_NotDetectedRetry, ex);
                }
                _logger.LogError(ex, "eToken is present but signing failed with an unrecognized error; certificate disabled to protect the PIN retry counter");
                throw Disable(PinFailureKind.Unrecognized, ex);
            }
        }
    }

    /// <summary>
    /// token 上有没有这个密钥容器。只用 CRYPT_VERIFYCONTEXT 枚举容器名：不打开私钥、不用 PIN。
    /// 只用来判断"签名失败是不是因为没插卡"，不用来拦截签名
    /// </summary>
    protected virtual bool IsTokenPresent()
    {
        try
        {
            return CryptoApi.EnumerateContainers(_cspProvider).Contains(_keyContainer);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            return false;
        }
    }

    private volatile bool _tokenPresent = true;

    /// <summary>最近一次检查或签名时 token 是否插着（只用于状态显示）</summary>
    public bool TokenPresent => _tokenPresent;

    /// <summary>重新检查 token 是否插着（状态显示用，不访问私钥、不用 PIN）</summary>
    public bool RefreshTokenPresence() => _tokenPresent = IsTokenPresent();

    private PinRejectedException Disable(PinFailureKind kind, Exception? inner)
    {
        var failure = new PinFailure(kind, DateTime.UtcNow);
        _cachedPin = null;
        _failure = failure;
        return new PinRejectedException(failure, justDisabled: true, inner);
    }

    /// <summary>用指定 PIN 打开容器并签名；PIN 错误时 CSP 抛出 HResult 为 SCARD_W_* 的异常</summary>
    protected virtual byte[] SignWithPin(string pin, byte[] hash, HashAlgorithmName hashAlgorithm)
    {
        using var rsa = CreateRsa(pin);
        return rsa.SignHash(hash, hashAlgorithm, RSASignaturePadding.Pkcs1);
    }

    /// <summary>获取证书公钥（DER 编码）</summary>
    public virtual byte[] GetCertificate()
    {
        using var store = new X509Store(StoreName.My, StoreLocation.LocalMachine);
        store.Open(OpenFlags.ReadOnly);

        var cert = store.Certificates
            .Find(X509FindType.FindByThumbprint, _thumbprint, false)
            .FirstOrDefault();

        if (cert == null)
            throw new InvalidOperationException($"Certificate {_thumbprint} not found");

        return cert.Export(X509ContentType.Cert);
    }

    private RSACryptoServiceProvider CreateRsa(string pin)
    {
        var containerWithPin = $"[{{{{{pin}}}}}]={_keyContainer}";
        var csp = new CspParameters
        {
            ProviderName = _cspProvider,
            KeyContainerName = containerWithPin,
            ProviderType = 1, // PROV_RSA_FULL
            Flags = CspProviderFlags.UseExistingKey | CspProviderFlags.UseMachineKeyStore
        };

        // 不能设 PersistKeyInCsp = false：Dispose 时 .NET 会对该容器调用 CRYPT_DELETEKEYSET，
        // 带着 PIN 的容器名会让 eToken 真的删掉私钥
        return new RSACryptoServiceProvider(csp);
    }
}
