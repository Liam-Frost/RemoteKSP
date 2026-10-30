using System;
using System.Collections.Generic;
using System.Security.Cryptography;

namespace RemoteKSP.Server.Core;

/// <summary>eToken 拒绝 PIN 的原因</summary>
public enum PinFailureKind
{
    /// <summary>SCARD_W_WRONG_CHV：PIN 错误，token 已扣一次重试次数</summary>
    Wrong,
    /// <summary>SCARD_W_CHV_BLOCKED：重试次数用完，token 已锁</summary>
    Locked,
    /// <summary>
    /// token 插着，但签名失败的错误码既不是上面两个，也不是已知的"没插卡/暂时忙"。
    /// 可能是 CSP 用别的错误码表示 PIN 错误：不再拿同一个 PIN 去试，免得把 token 试锁
    /// </summary>
    Unrecognized
}

/// <summary>证书因 PIN 被拒而停用的记录</summary>
public record PinFailure(PinFailureKind Kind, DateTime OccurredAt)
{
    public const uint ScardWrongChv = 0x8010006B;
    public const uint ScardChvBlocked = 0x8010006C;

    // 这几个错误发生在校验 PIN 之前（读卡器里没有卡、卡被别的程序占用、等卡超时），token 的重试计数不会减少
    public const uint ScardNoSmartcard = 0x8010000C;
    public const uint ScardSharingViolation = 0x8010000B;
    public const uint ScardTimeout = 0x8010000A;

    /// <summary>按 HRESULT 识别 PIN 错误/锁卡；其他错误（未插入、驱动问题等）返回 null</summary>
    public static PinFailureKind? Classify(Exception ex)
    {
        for (var e = ex; e != null; e = e.InnerException)
        {
            switch ((uint)e.HResult)
            {
                case ScardWrongChv: return PinFailureKind.Wrong;
                case ScardChvBlocked: return PinFailureKind.Locked;
            }
        }
        return null;
    }

    /// <summary>错误码明确表示还没走到校验 PIN（没卡、卡忙、超时）：可以放心等下一次请求再试</summary>
    public static bool IsBeforePinCheck(Exception ex)
    {
        for (var e = ex; e != null; e = e.InnerException)
        {
            if ((uint)e.HResult is ScardNoSmartcard or ScardSharingViolation or ScardTimeout)
                return true;
        }
        return false;
    }

    /// <summary>停用原因（GUI、客户端、Windows 消息共用）</summary>
    public string Reason => Kind switch
    {
        PinFailureKind.Locked => "eToken 已锁定（PIN 重试次数已用完）",
        PinFailureKind.Wrong => "服务端保存的 PIN 错误",
        _ => "eToken 返回了无法识别的错误（可能是 PIN 错误）"
    };

    /// <summary>排除问题的操作建议</summary>
    public string Action => Kind switch
    {
        PinFailureKind.Locked => "请用 SafeNet Authentication Client 解锁 eToken，然后在服务端 GUI 重新验证 PIN",
        PinFailureKind.Wrong => "请在服务端 GUI「证书管理」中重新验证 PIN",
        _ => "为避免把 eToken 试锁，已停止使用保存的 PIN。请检查 eToken 是否正常（SafeNet Authentication Client 能否登录），" +
             "然后在服务端 GUI「证书管理」中重新验证 PIN；活动日志里有原始错误"
    };

    public string Detail => $"已停用：{Reason}（{OccurredAt.ToLocalTime():yyyy-MM-dd HH:mm:ss}）。{Action}";
}

/// <summary>证书因 PIN 被拒停用；<see cref="JustDisabled"/> 表示这次请求触发了停用（只有这一次真正访问了 token）</summary>
public class PinRejectedException : CryptographicException
{
    public PinFailure Failure { get; }
    public bool JustDisabled { get; }

    public PinRejectedException(PinFailure failure, bool justDisabled, Exception? inner = null)
        : base($"证书已停用：{failure.Reason}。{failure.Action}", inner)
    {
        Failure = failure;
        JustDisabled = justDisabled;
    }
}

/// <summary>验证 PIN 的结果；失败时 <see cref="Failure"/> 为 null 表示不是 PIN 本身的问题</summary>
public record PinVerifyResult(bool Success, PinFailureKind? Failure, string? Error);

/// <summary>
/// GUI 验证 PIN 的连续失败上限：同一个 eToken 容器连续失败 <see cref="MaxFailures"/> 次后锁定
/// <see cref="LockoutDuration"/>，期间不再访问 token，避免把 token 的重试次数耗光。
/// 放在服务进程里，重开 GUI 不会清零。
/// </summary>
public class PinAttemptLimiter
{
    public const int MaxFailures = 3;
    public static readonly TimeSpan LockoutDuration = TimeSpan.FromMinutes(1);

    private readonly TimeProvider _clock;
    private readonly object _lock = new();
    private readonly Dictionary<string, (int Failures, DateTimeOffset? LockedUntil)> _state = new();

    public PinAttemptLimiter(TimeProvider? clock = null) => _clock = clock ?? TimeProvider.System;

    public static string KeyFor(string? cspProvider, string? keyContainer) =>
        $"{cspProvider}|{keyContainer}".ToLowerInvariant();

    /// <summary>锁定中返回剩余时间，否则返回 null；锁定到期后重新给 <see cref="MaxFailures"/> 次机会</summary>
    public TimeSpan? GetLockout(string key)
    {
        lock (_lock)
        {
            if (!_state.TryGetValue(key, out var s) || s.LockedUntil is not { } until)
                return null;
            var remaining = until - _clock.GetUtcNow();
            if (remaining > TimeSpan.Zero)
                return remaining;
            _state.Remove(key);
            return null;
        }
    }

    /// <summary>记一次失败；返回还剩几次机会，0 表示已进入锁定</summary>
    public int RecordFailure(string key)
    {
        lock (_lock)
        {
            var failures = (_state.TryGetValue(key, out var s) ? s.Failures : 0) + 1;
            if (failures >= MaxFailures)
            {
                _state[key] = (failures, _clock.GetUtcNow() + LockoutDuration);
                return 0;
            }
            _state[key] = (failures, null);
            return MaxFailures - failures;
        }
    }

    public void RecordSuccess(string key)
    {
        lock (_lock)
            _state.Remove(key);
    }
}
