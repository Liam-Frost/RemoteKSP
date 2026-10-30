using System;
using System.IO;
using System.IO.Pipes;
using System.Text.Json;
using System.Threading.Tasks;

namespace RemoteKSP.Server.Services;

/// <summary>命名管道客户端（GUI使用）</summary>
public class PipeClient
{
    private const string PipeName = "RemoteKSP_Control";
    private readonly ActivityLogService? _activityLog;

    public PipeClient(ActivityLogService? activityLog = null)
    {
        _activityLog = activityLog;
    }

    public async Task<T?> SendCommandAsync<T>(string command, object? data = null)
    {
        using var pipe = new NamedPipeClientStream(".", PipeName, PipeDirection.InOut);

        try
        {
            await pipe.ConnectAsync(2000);
        }
        catch (UnauthorizedAccessException ex)
        {
            _activityLog?.Error("管道", "无权访问服务管道", "GUI 需要以管理员身份运行");
            throw new PipeConnectionException("无权访问服务管道（GUI 需要以管理员身份运行）", accessDenied: true, ex);
        }
        catch (Exception ex)
        {
            _activityLog?.Error("管道", "服务未运行或无法连接", ex.Message);
            throw new PipeConnectionException($"服务未运行或无法连接: {ex.Message}", accessDenied: false, ex);
        }

        // leaveOpen：管道由外层 using 释放；否则 reader 先关闭管道，writer 释放时 Flush 会抛 "Cannot access a closed pipe"
        using var writer = new StreamWriter(pipe, leaveOpen: true) { AutoFlush = true };
        using var reader = new StreamReader(pipe, leaveOpen: true);

        var request = new { Command = command, Data = data != null ? JsonSerializer.Serialize(data) : null };
        await writer.WriteLineAsync(JsonSerializer.Serialize(request));

        var responseLine = await reader.ReadLineAsync();
        if (responseLine == null)
        {
            _activityLog?.Error("管道", "服务无响应", command);
            throw new PipeConnectionException("服务无响应", accessDenied: false);
        }

        var response = JsonSerializer.Deserialize<PipeResponse>(responseLine);
        if (response == null || !response.Success)
        {
            _activityLog?.Error("管道", $"命令失败: {command}", response?.Error ?? "Unknown error");
            throw new InvalidOperationException(response?.Error ?? "Unknown error");
        }

        _activityLog?.Info("管道", $"命令成功: {command}");

        if (response.Data == null)
            return default;

        return JsonSerializer.Deserialize<T>(response.Data);
    }

    record PipeResponse(bool Success, string? Data, string? Error);
}

/// <summary>
/// 管理通道本身不可用（连不上、无权访问、连上后无响应），区别于服务处理命令时返回的错误。
/// 界面据此显示「管理通道不可连接」，而不是把它当成服务未运行
/// </summary>
public class PipeConnectionException : InvalidOperationException
{
    /// <summary>管道 ACL 只允许管理员和 SYSTEM：GUI 未以管理员身份运行</summary>
    public bool AccessDenied { get; }

    public PipeConnectionException(string message, bool accessDenied, Exception? inner = null) : base(message, inner)
    {
        AccessDenied = accessDenied;
    }
}
