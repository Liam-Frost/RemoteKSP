using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography.X509Certificates;
using Microsoft.Extensions.Logging.Abstractions;
using RemoteKSP.Shared;

namespace RemoteKSP.Server.Core;

/// <summary>默认证书状态（/info 兼容字段，GUI 概览也用）</summary>
public record KeySourceStatus(string Mode, string Thumbprint, string Detail);

public enum RetryOutcome
{
    /// <summary>没有不可用的证书</summary>
    NothingToRetry,
    /// <summary>本轮之后全部可用</summary>
    AllRecovered,
    /// <summary>本轮之后仍有证书不可用</summary>
    StillFailing,
    /// <summary>已达到重试上限，本轮没有加载</summary>
    Exhausted,
    /// <summary>重试期间配置被重新加载，本轮结果作废</summary>
    Superseded
}

/// <param name="Remaining">仍不可用的证书数量</param>
/// <param name="Attempt">本轮是第几次重试</param>
/// <param name="Recovered">本轮恢复的证书名称</param>
public record RetryResult(RetryOutcome Outcome, int Remaining, int Attempt, IReadOnlyList<string> Recovered);

/// <summary>单个证书的运行时状态（GUI 通过管道读取）</summary>
/// <param name="DisabledReason">非 null 表示证书因 PIN 被拒停用</param>
public record CertificateStatus(
    string Id, string Name, string Thumbprint, string SourceType, bool IsShared, bool IsDefault,
    bool IsReady, bool NeedsPin, string Detail, string? Subject, DateTime? NotAfter, DateTime AddedAt,
    string? CspProvider, string? KeyContainer, string? StoreLocation, string? PfxPath,
    string? DisabledReason = null);

/// <summary>证书因 PIN 被拒停用（每次停用只触发一次）</summary>
public record PinRejectedEvent(string CertificateId, string CertificateName, string Thumbprint, PinFailure Failure);

/// <summary>
/// 管理证书注册表里每张证书对应的密钥源。单张证书初始化失败只影响它自己，
/// 不会拖累其他证书。注册表或 PIN 变化后调用 <see cref="Reload"/>。
/// </summary>
public class KeySourceManager
{
    public const string ModeTest = "test";
    public const string ModeProduction = "production";

    private readonly object _lock = new();
    private List<LoadedCert> _loaded = new();

    // 加载失败证书的自动重试状态。Reload 代表配置变了，重试次数清零
    private int _generation;
    private int _retryAttempts;
    private int _maxRetryAttempts;

    private readonly Func<CertificateEntry, IKeySource> _createKeySource;

    public CertificateRegistry Registry { get; }

    /// <summary>签名时 token 拒绝 PIN、证书刚被停用</summary>
    public event Action<PinRejectedEvent>? PinRejected;

    /// <summary>同一张不可用的证书，签名请求触发重新加载的最短间隔（加载只读证书存储，不访问 token 私钥）</summary>
    public static readonly TimeSpan DefaultOnDemandReloadInterval = TimeSpan.FromSeconds(10);

    private readonly TimeSpan _onDemandReloadInterval;
    private readonly Dictionary<string, DateTime> _lastOnDemandReload = new();

    public KeySourceManager(CertificateRegistry registry) : this(registry, CreateKeySource) { }

    /// <param name="createKeySource">测试用：替换真实的密钥源（eToken 需要硬件）</param>
    /// <param name="onDemandReloadInterval">测试用：签名触发重新加载的最短间隔，默认 10 秒</param>
    public KeySourceManager(CertificateRegistry registry, Func<CertificateEntry, IKeySource> createKeySource,
        TimeSpan? onDemandReloadInterval = null)
    {
        Registry = registry;
        _createKeySource = createKeySource;
        _onDemandReloadInterval = onDemandReloadInterval ?? DefaultOnDemandReloadInterval;
        Reload();
    }

    /// <summary>设置自动重试上限，仅用于状态说明；由 KeySourceRetryService 在启动时设置</summary>
    public void ConfigureRetry(int maxAttempts)
    {
        lock (_lock)
            _maxRetryAttempts = maxAttempts;
    }

    /// <summary>
    /// 重新加载此刻仍不可用的证书（例如开机时 eToken 驱动还没就绪）。已就绪的证书不动。
    /// 达到上限后不再加载，直到下一次 Reload（GUI 改动证书或重启服务）重置次数。
    /// </summary>
    public RetryResult RetryFailed(int maxAttempts)
    {
        int generation, attempt;
        List<CertificateEntry> failed;
        lock (_lock)
        {
            failed = _loaded.Where(l => l.Source == null).Select(l => l.Entry).ToList();
            if (failed.Count == 0)
                return new RetryResult(RetryOutcome.NothingToRetry, 0, _retryAttempts, []);
            if (_retryAttempts >= maxAttempts)
                return new RetryResult(RetryOutcome.Exhausted, failed.Count, _retryAttempts, []);
            attempt = ++_retryAttempts;
            generation = _generation;
        }

        // 加载可能访问硬件，放在锁外，签名请求不被阻塞
        var results = failed.Select(Load).ToList();

        lock (_lock)
        {
            // 重试期间发生过 Reload：结果基于旧配置，丢弃
            if (_generation != generation)
                return new RetryResult(RetryOutcome.Superseded, 0, 0, []);

            var recovered = new List<string>();
            _loaded = _loaded.Select(l =>
            {
                var replacement = results.FirstOrDefault(r => ReferenceEquals(r.Entry, l.Entry));
                if (replacement?.Source == null)
                    return l;
                recovered.Add(l.Entry.Name);
                return replacement;
            }).ToList();

            var remaining = _loaded.Count(l => l.Source == null);
            var outcome = remaining == 0 ? RetryOutcome.AllRecovered : RetryOutcome.StillFailing;
            return new RetryResult(outcome, remaining, attempt, recovered);
        }
    }

    /// <summary>失败证书详情后附加的重试进度</summary>
    private string RetryNote()
    {
        if (_maxRetryAttempts <= 0)
            return "";
        return _retryAttempts >= _maxRetryAttempts
            ? $"（已自动重试 {_retryAttempts} 次仍失败，已停止重试；排除问题后请重启服务）"
            : $"（正在自动重试 {_retryAttempts}/{_maxRetryAttempts}）";
    }

    private class LoadedCert
    {
        public required CertificateEntry Entry { get; init; }
        public IKeySource? Source { get; init; }
        public bool IsReady { get; init; }
        public bool NeedsPin { get; init; }
        public string Detail { get; init; } = "";
        public string? Subject { get; init; }
        public DateTime? NotAfter { get; init; }
    }

    public void Reload()
    {
        var loaded = Registry.GetAll().Select(Load).ToList();
        lock (_lock)
        {
            _loaded = loaded;
            _generation++;
            _retryAttempts = 0;
            _lastOnDemandReload.Clear();
        }
    }

    private LoadedCert Load(CertificateEntry entry)
    {
        try
        {
            var source = _createKeySource(entry);
            if (source is ETokenKeySource etoken && entry.PinFailure is { } kind)
                etoken.RestoreFailure(new PinFailure(kind, entry.PinFailedAt ?? DateTime.UtcNow));
            using var cert = X509CertificateLoader.LoadCertificate(source.GetCertificate());
            var needsPin = source is ETokenKeySource { HasPin: false };
            // 只读容器名（不打开私钥、不用 PIN），结果只用于状态显示，不影响能否签名
            if (source is ETokenKeySource token)
                token.RefreshTokenPresence();
            return new LoadedCert
            {
                Entry = entry,
                Source = source,
                IsReady = !needsPin && entry.SourceType != "test",
                NeedsPin = needsPin,
                Detail = entry.SourceType switch
                {
                    "test" => "测试证书：可验证配对和下发，签名会被拒绝",
                    _ when needsPin => "尚未验证 PIN，签名会失败",
                    _ => "就绪"
                },
                Subject = cert.Subject,
                NotAfter = cert.NotAfter.ToUniversalTime()
            };
        }
        catch (Exception ex)
        {
            return new LoadedCert { Entry = entry, Detail = $"不可用：{ex.Message}" };
        }
    }

    /// <summary>/info 里的兼容证书：分享列表里的第一张</summary>
    public CertificateEntry? DefaultEntry
    {
        get
        {
            lock (_lock)
                return _loaded.FirstOrDefault(l => l.Entry.IsShared)?.Entry;
        }
    }

    public KeySourceStatus GetStatus()
    {
        lock (_lock)
        {
            var def = _loaded.FirstOrDefault(l => l.Entry.IsShared);
            var shared = _loaded.Count(l => l.Entry.IsShared);
            var summary = $"共 {_loaded.Count} 个证书，{shared} 个已分享";
            if (def == null)
                return new KeySourceStatus(ModeTest, "", $"{summary}。没有分享的证书，客户端无法获取证书");

            var mode = def.Entry.SourceType == "test" || def.Source == null ? ModeTest : ModeProduction;
            return new KeySourceStatus(mode, def.Entry.Thumbprint, $"{summary}。默认证书「{def.Entry.Name}」：{DetailOf(def)}");
        }
    }

    /// <summary>停用状态是签名时才发生的，必须实时读密钥源，不能用 Reload 时缓存的结果</summary>
    private static PinFailure? FailureOf(LoadedCert l) => (l.Source as ETokenKeySource)?.Failure;

    private static string DetailOf(LoadedCert l) => FailureOf(l)?.Detail ?? l.Detail;

    private const string TokenAbsentDetail = "未检测到 eToken（未插入或驱动未就绪）。插入后签名请求会直接使用，不用重启服务";

    public List<CertificateStatus> GetCertificateStatuses()
    {
        // 之前没检测到的 token 重新看一眼（只读容器名，不碰私钥），插上后状态不用等签名就能恢复。放在锁外
        List<ETokenKeySource> absent;
        lock (_lock)
            absent = _loaded.Select(l => l.Source).OfType<ETokenKeySource>().Where(t => !t.TokenPresent).ToList();
        foreach (var token in absent)
            token.RefreshTokenPresence();

        lock (_lock)
        {
            var defaultId = _loaded.FirstOrDefault(l => l.Entry.IsShared)?.Entry.Id;
            var retryNote = RetryNote();
            return _loaded.Select(l =>
            {
                var failure = FailureOf(l);
                var tokenAbsent = l.Source is ETokenKeySource { TokenPresent: false } && failure == null;
                return new CertificateStatus(
                    l.Entry.Id, l.Entry.Name, l.Entry.Thumbprint, l.Entry.SourceType, l.Entry.IsShared,
                    l.Entry.Id == defaultId, l.IsReady && failure == null && !tokenAbsent, l.NeedsPin && failure == null,
                    l.Source == null ? l.Detail + retryNote : tokenAbsent ? TokenAbsentDetail : DetailOf(l),
                    l.Subject, l.NotAfter, l.Entry.AddedAt,
                    l.Entry.CspProvider, l.Entry.KeyContainer, l.Entry.StoreLocation, l.Entry.PfxPath,
                    failure?.Reason);
            }).ToList();
        }
    }

    /// <summary>证书的停用记录（未停用或不是 eToken 返回 null）</summary>
    public PinFailure? GetPinFailure(string thumbprint)
    {
        var normalized = Protocol.NormalizeThumbprint(thumbprint);
        lock (_lock)
        {
            var l = _loaded.FirstOrDefault(x => x.Entry.Thumbprint == normalized);
            return l == null ? null : FailureOf(l);
        }
    }

    /// <summary>按指纹查找证书条目</summary>
    public CertificateEntry? Find(string thumbprint)
    {
        var normalized = Protocol.NormalizeThumbprint(thumbprint);
        lock (_lock)
            return _loaded.FirstOrDefault(l => l.Entry.Thumbprint == normalized)?.Entry;
    }

    /// <summary>证书主题和有效期（Reload 时缓存）</summary>
    public (string? Subject, DateTime? NotAfter) GetCertificateInfo(string thumbprint)
    {
        lock (_lock)
        {
            var l = _loaded.FirstOrDefault(x => x.Entry.Thumbprint == Protocol.NormalizeThumbprint(thumbprint));
            return (l?.Subject, l?.NotAfter);
        }
    }

    public byte[] GetCertificateDer(string thumbprint) => GetSource(thumbprint).Source!.GetCertificate();

    /// <summary>
    /// 签名；测试证书抛 <see cref="TestModeException"/>，PIN 被拒抛 <see cref="PinRejectedException"/>。
    /// 刚被停用时删除保存的 PIN、记下原因并触发 <see cref="PinRejected"/>
    /// </summary>
    public byte[] SignHash(string thumbprint, byte[] hash, string hashAlgorithm)
    {
        var loaded = GetSource(thumbprint);
        try
        {
            return loaded.Source!.SignHash(hash, hashAlgorithm);
        }
        catch (PinRejectedException ex) when (ex.JustDisabled)
        {
            lock (_lock)
            {
                // 签名期间重新加载过（例如刚设置了新 PIN）：不要用旧结果覆盖新配置
                if (_loaded.Contains(loaded))
                {
                    try { Registry.MarkPinFailure(loaded.Entry.Id, ex.Failure); }
                    catch { /* 写盘失败：内存里已停用，重启前不会再访问 token */ }
                }
            }
            PinRejected?.Invoke(new PinRejectedEvent(loaded.Entry.Id, loaded.Entry.Name, loaded.Entry.Thumbprint, ex.Failure));
            throw;
        }
    }

    /// <summary>
    /// 取签名用的密钥源。证书当前不可用（例如证书刚导入存储、驱动刚就绪）时，按请求重新加载一次再判断，
    /// 不必等自动重试或重启服务。加载只读证书存储和容器名，不访问 token 私钥、不用 PIN；同一张证书最多每 10 秒一次
    /// </summary>
    private LoadedCert GetSource(string thumbprint)
    {
        var normalized = Protocol.NormalizeThumbprint(thumbprint);
        LoadedCert current;
        int generation;
        lock (_lock)
        {
            current = _loaded.FirstOrDefault(x => x.Entry.Thumbprint == normalized)
                ?? throw new KeyNotFoundException($"服务端没有指纹为 {normalized} 的证书");
            if (current.Source != null)
                return current;
            var now = DateTime.UtcNow;
            if (_lastOnDemandReload.TryGetValue(current.Entry.Id, out var last) && now - last < _onDemandReloadInterval)
                throw new InvalidOperationException($"证书「{current.Entry.Name}」{current.Detail}");
            _lastOnDemandReload[current.Entry.Id] = now;
            generation = _generation;
        }

        var reloaded = Load(current.Entry);

        lock (_lock)
        {
            // 期间配置被重新加载过，或别的请求已经换掉了这一项：以现在的为准
            if (_generation == generation && reloaded.Source != null)
            {
                var index = _loaded.IndexOf(current);
                if (index >= 0)
                    _loaded[index] = reloaded;
            }
            var latest = _loaded.FirstOrDefault(x => x.Entry.Thumbprint == normalized) ?? reloaded;
            return latest.Source != null ? latest : throw new InvalidOperationException($"证书「{latest.Entry.Name}」{latest.Detail}");
        }
    }

    /// <summary>按条目创建密钥源，并提前检查证书和私钥是否可用</summary>
    public static IKeySource CreateKeySource(CertificateEntry entry)
    {
        switch (entry.SourceType)
        {
            case "test":
                if (string.IsNullOrEmpty(entry.TestCertDer))
                    throw new InvalidOperationException("测试证书数据缺失");
                return new TestKeySource(Convert.FromBase64String(entry.TestCertDer));

            case "store":
            {
                var location = string.Equals(entry.StoreLocation, "CurrentUser", StringComparison.OrdinalIgnoreCase)
                    ? StoreLocation.CurrentUser
                    : StoreLocation.LocalMachine;
                var source = new StoreKeySource(location, entry.Thumbprint);
                _ = source.GetCertificate();
                return source;
            }

            case "pfx":
            {
                if (string.IsNullOrEmpty(entry.PfxPath))
                    throw new InvalidOperationException("未指定 PFX 文件");
                var pfxPath = Path.IsPathRooted(entry.PfxPath)
                    ? entry.PfxPath
                    : Path.Combine(ServerPaths.DataDir, entry.PfxPath);
                var password = string.IsNullOrEmpty(entry.PfxPasswordEncrypted)
                    ? ""
                    : DpapiHelper.UnprotectString(entry.PfxPasswordEncrypted);
                var source = new PfxKeySource(pfxPath, password, entry.Thumbprint);
                _ = source.GetCertificate();
                return source;
            }

            case "etoken":
            {
                var csp = string.IsNullOrWhiteSpace(entry.CspProvider)
                    ? "eToken Base Cryptographic Provider"
                    : entry.CspProvider;
                if (string.IsNullOrWhiteSpace(entry.KeyContainer))
                    throw new InvalidOperationException("未指定密钥容器");

                var source = new ETokenKeySource(csp, entry.KeyContainer, entry.Thumbprint,
                    NullLogger<ETokenKeySource>.Instance);

                // eToken 证书必须在 LocalMachine\My：服务以 SYSTEM 运行，读不到当前用户存储
                try
                {
                    _ = source.GetCertificate();
                }
                catch (Exception ex)
                {
                    throw new InvalidOperationException($"证书 {entry.Thumbprint} 不在 LocalMachine\\My", ex);
                }

                // 不按容器枚举结果拦截：没插 token 时照样加载，签名时直接访问 token，
                // 插上后下一个请求就能签。枚举结果只用于状态显示

                if (!string.IsNullOrEmpty(entry.PinEncrypted))
                {
                    try { source.SetPin(DpapiHelper.UnprotectString(entry.PinEncrypted)); }
                    catch { /* PIN 解密失败按未设置处理 */ }
                }
                return source;
            }

            default:
                throw new InvalidOperationException($"未知的密钥源类型 {entry.SourceType}");
        }
    }
}
