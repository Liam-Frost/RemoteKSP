using System;
using System.Collections.Generic;
using System.Linq;

namespace RemoteKSP.Client.Core;

/// <summary>证书同步操作的详细结果，包含成功和失败信息</summary>
public class CertificateSyncReport
{
    public List<ServerSyncResult> Results { get; } = [];

    public int SuccessCount => Results.Count(r => r.IsSuccess);
    public int FailureCount => Results.Count(r => !r.IsSuccess);
    public bool AllSucceeded => Results.All(r => r.IsSuccess);
    public bool AllFailed => Results.All(r => !r.IsSuccess);
    public bool PartialSuccess => SuccessCount > 0 && FailureCount > 0;
}

/// <summary>单个服务端的同步结果</summary>
public class ServerSyncResult
{
    public string ServerName { get; set; } = "";
    public bool IsSuccess { get; set; }
    public SyncResult? SyncResult { get; set; }
    public SyncError? Error { get; set; }
}

/// <summary>同步失败的结构化错误信息</summary>
public class SyncError
{
    public SyncErrorType Type { get; set; }
    public string Summary { get; set; } = "";
    public string? Detail { get; set; }
    public int? HttpStatusCode { get; set; }
    public string? ProblemType { get; set; }
    public string? ProblemTitle { get; set; }
    public string? ProblemDetail { get; set; }
    public string? RawError { get; set; }
}

/// <summary>同步错误类型</summary>
public enum SyncErrorType
{
    NetworkError,       // 服务器不可达
    HttpError,          // HTTP 错误（非 2xx）
    AuthenticationError,// 认证失败
    KeySourceNotReady,  // 密钥源未就绪
    ServerError,        // 服务器内部错误
    Unknown             // 未知错误
}
