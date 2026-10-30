using System;
using System.Collections.Generic;

namespace RemoteKSP.Server.Core;

/// <summary>配对请求</summary>
public class PairingRequest
{
    public string RequestId { get; set; } = Guid.NewGuid().ToString("N");
    public string MachineName { get; set; } = "";
    public string IpAddress { get; set; } = "";
    public string ClientNonce { get; set; } = "";
    public string ServerNonce { get; set; } = Guid.NewGuid().ToString("N");
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime ExpiresAt { get; set; } = DateTime.UtcNow.AddMinutes(5);
    public PairingStatus Status { get; set; } = PairingStatus.Pending;
    public string? GeneratedSecret { get; set; }
}

public enum PairingStatus
{
    Pending,
    Approved,
    Rejected,
    Expired,
    Consumed
}

/// <summary>配对管理器</summary>
public class PairingManager
{
    private readonly List<PairingRequest> _pendingRequests = new();
    private readonly object _lock = new();
    private readonly TimeProvider _time;

    public PairingManager(TimeProvider? time = null) => _time = time ?? TimeProvider.System;

    // 状态读取返回副本，调用方不能修改状态或从查询接口取得凭据。
    private static PairingRequest Snapshot(PairingRequest r) => new()
    {
        RequestId = r.RequestId, MachineName = r.MachineName, IpAddress = r.IpAddress,
        ClientNonce = r.ClientNonce, ServerNonce = r.ServerNonce,
        CreatedAt = r.CreatedAt, ExpiresAt = r.ExpiresAt, Status = r.Status
    };

    private void Expire(PairingRequest request)
    {
        if (request.ExpiresAt <= _time.GetUtcNow().UtcDateTime)
        {
            request.Status = PairingStatus.Expired;
            request.GeneratedSecret = null;
        }
    }

    /// <summary>创建配对请求</summary>
    public PairingRequest CreateRequest(string machineName, string ipAddress, string clientNonce)
    {
        lock (_lock)
        {
            // 清理过期请求
            _pendingRequests.RemoveAll(r => r.ExpiresAt <= _time.GetUtcNow().UtcDateTime);

            // 同一 IP 重新发起配对：丢弃旧的待处理请求，避免旧 nonce 导致两边验证码不一致
            _pendingRequests.RemoveAll(r => r.IpAddress == ipAddress && r.Status == PairingStatus.Pending);

            var request = new PairingRequest
            {
                MachineName = machineName,
                IpAddress = ipAddress,
                ClientNonce = clientNonce,
                CreatedAt = _time.GetUtcNow().UtcDateTime,
                ExpiresAt = _time.GetUtcNow().UtcDateTime.AddMinutes(5)
            };

            _pendingRequests.Add(request);
            return Snapshot(request);
        }
    }

    /// <summary>获取待处理请求</summary>
    public List<PairingRequest> GetPendingRequests()
    {
        lock (_lock)
        {
            _pendingRequests.RemoveAll(r => r.ExpiresAt <= _time.GetUtcNow().UtcDateTime);
            return _pendingRequests.FindAll(r => r.Status == PairingStatus.Pending).ConvertAll(Snapshot);
        }
    }

    /// <summary>批准配对</summary>
    public void Approve(string requestId, string generatedSecret)
    {
        lock (_lock)
        {
            var request = _pendingRequests.Find(r => r.RequestId == requestId);
            if (request != null) Expire(request);
            if (request == null || request.Status != PairingStatus.Pending)
                throw new InvalidOperationException("配对请求已过期或已处理，请重新发起配对。");
            request.Status = PairingStatus.Approved;
            request.GeneratedSecret = generatedSecret;
        }
    }

    /// <summary>拒绝配对</summary>
    public void Reject(string requestId)
    {
        lock (_lock)
        {
            var request = _pendingRequests.Find(r => r.RequestId == requestId);
            if (request != null) Expire(request);
            if (request != null && request.Status == PairingStatus.Pending)
            {
                request.Status = PairingStatus.Rejected;
            }
        }
    }

    /// <summary>获取请求状态</summary>
    public PairingRequest? GetRequest(string requestId)
    {
        lock (_lock)
        {
            var request = _pendingRequests.Find(r => r.RequestId == requestId);
            if (request == null) return null;
            Expire(request);
            return Snapshot(request);
        }
    }

    /// <summary>锁内登记客户端并一次性领取凭据；登记失败时允许重试。</summary>
    public PairingClaim? ClaimApprovedRequest(string requestId, Action<PairingClaim> register)
    {
        lock (_lock)
        {
            var request = _pendingRequests.Find(r => r.RequestId == requestId);
            if (request != null) Expire(request);
            if (request == null || request.Status != PairingStatus.Approved || request.GeneratedSecret == null)
                return null;

            var claim = new PairingClaim(Guid.NewGuid().ToString("N"), request.GeneratedSecret,
                request.MachineName, request.IpAddress);
            register(claim);
            request.Status = PairingStatus.Consumed;
            request.GeneratedSecret = null;
            return claim;
        }
    }
}

public record PairingClaim(string ClientId, string Secret, string MachineName, string IpAddress);
