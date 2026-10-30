using System;
using System.Collections.Generic;
using System.Globalization;
using RemoteKSP.Shared;

namespace RemoteKSP.Server.Core;

public sealed class RequestAuth
{
    private readonly TimeProvider _time;
    private readonly int _capacity;
    private readonly object _lock = new();
    private readonly HashSet<(string ClientId, string Nonce)> _seen = new();
    private readonly PriorityQueue<(string ClientId, string Nonce), long> _expiry = new();

    public RequestAuth(TimeProvider? time = null, int capacity = 100_000)
    {
        _time = time ?? TimeProvider.System;
        _capacity = capacity > 0 ? capacity : throw new ArgumentOutOfRangeException(nameof(capacity));
    }

    public bool VerifySignature(string method, string path, string timestamp,
        string hashAlgorithm, string clientId, string bodyBase64, string nonce,
        string providedSignature, string clientSecret)
    {
        if (!long.TryParse(timestamp, NumberStyles.None, CultureInfo.InvariantCulture, out var ts) ||
            nonce.Length != 32 || !IsLowerHex(nonce))
            return false;

        var now = _time.GetUtcNow().ToUnixTimeSeconds();
        if (ts < now - Protocol.MaxClockSkewSeconds || ts > now + Protocol.MaxClockSkewSeconds)
            return false;

        var canonical = Protocol.Canonical(method, path, timestamp, hashAlgorithm, clientId, bodyBase64, nonce);
        if (!Protocol.SignatureEquals(Protocol.ComputeSignature(clientSecret, canonical), providedSignature))
            return false;

        lock (_lock)
        {
            now = _time.GetUtcNow().ToUnixTimeSeconds();
            if (ts < now - Protocol.MaxClockSkewSeconds || ts > now + Protocol.MaxClockSkewSeconds)
                return false;
            // 包括客户端时钟领先的情况，保留至时间戳完全超出允许窗口。
            while (_expiry.TryPeek(out var expired, out var expiresAt) && expiresAt < now)
            {
                _expiry.Dequeue();
                _seen.Remove(expired);
            }
            var key = (clientId, nonce);
            if (_seen.Contains(key) || _seen.Count >= _capacity)
                return false; // 满时拒绝新请求，不能驱逐仍有效的 nonce。
            _seen.Add(key);
            _expiry.Enqueue(key, ts + Protocol.MaxClockSkewSeconds);
            return true;
        }
    }

    private static bool IsLowerHex(string value)
    {
        foreach (var c in value)
            if (!(c >= '0' && c <= '9') && !(c >= 'a' && c <= 'f'))
                return false;
        return true;
    }
}
