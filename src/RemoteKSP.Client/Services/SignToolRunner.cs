using System;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace RemoteKSP.Client.Services;

/// <summary>一次签名命令执行的结果</summary>
/// <param name="ExitCode">进程退出码；超时时为 null</param>
public sealed record SignToolRunResult(int? ExitCode, string Output, bool TimedOut, TimeSpan Elapsed)
{
    public bool Succeeded => ExitCode == 0;
}

/// <summary>
/// 执行 <see cref="SignToolCommandBuilder"/> 生成的命令。
/// 通过 cmd.exe /s /c 原样运行，和把命令粘贴进 CMD 窗口的行为一致，执行的就是预览里显示的那一行。
/// </summary>
public static class SignToolRunner
{
    /// <summary>远程签名要经过网络往返，时间戳服务器也可能较慢；超过此时间结束整个进程树</summary>
    public static readonly TimeSpan DefaultTimeout = TimeSpan.FromMinutes(10);

    [DllImport("kernel32.dll")]
    private static extern uint GetOEMCP();

    public static Task<SignToolRunResult> RunAsync(string command, string? workingDirectory = null,
        TimeSpan? timeout = null, CancellationToken cancellationToken = default) =>
        // 进程启动和读取管道都放到线程池，避免阻塞 UI 线程
        Task.Run(() => RunCoreAsync(command, workingDirectory, timeout ?? DefaultTimeout, cancellationToken),
            cancellationToken);

    private static async Task<SignToolRunResult> RunCoreAsync(string command, string? workingDirectory,
        TimeSpan timeout, CancellationToken cancellationToken)
    {
        // GUI 进程没有控制台，signtool 按 OEM 代码页（中文系统为 936）输出，用 UTF-8 解码会乱码
        var encoding = ConsoleEncoding();
        var psi = new ProcessStartInfo("cmd.exe", "/d /s /c \"" + command + "\"")
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = encoding,
            StandardErrorEncoding = encoding,
            WorkingDirectory = workingDirectory is { Length: > 0 } && Directory.Exists(workingDirectory)
                ? workingDirectory
                : Environment.GetFolderPath(Environment.SpecialFolder.System)
        };

        var output = new StringBuilder();
        var sync = new object();
        void Append(string? line)
        {
            if (line == null) return;
            lock (sync) output.AppendLine(line);
        }

        var stopwatch = Stopwatch.StartNew();
        using var process = new Process { StartInfo = psi };
        process.OutputDataReceived += (_, e) => Append(e.Data);
        process.ErrorDataReceived += (_, e) => Append(e.Data);
        process.Start();
        // 没有交互输入：立即关闭 stdin，命令若意外等待输入会直接结束而不是挂起
        process.StandardInput.Close();
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();

        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutCts.CancelAfter(timeout);
        try
        {
            await process.WaitForExitAsync(timeoutCts.Token);
        }
        catch (OperationCanceledException)
        {
            // 超时或调用方取消：cmd 下面还有 signtool，要结束整个进程树
            try { process.Kill(entireProcessTree: true); } catch (InvalidOperationException) { }
            await process.WaitForExitAsync(CancellationToken.None);
            string partial;
            lock (sync) partial = output.ToString();
            return new SignToolRunResult(null, partial, TimedOut: !cancellationToken.IsCancellationRequested,
                stopwatch.Elapsed);
        }

        // 无参 WaitForExit 会等异步读取把剩余输出读完
        process.WaitForExit();
        string text;
        lock (sync) text = output.ToString();
        return new SignToolRunResult(process.ExitCode, text, TimedOut: false, stopwatch.Elapsed);
    }

    private static Encoding ConsoleEncoding()
    {
        try
        {
            Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
            return Encoding.GetEncoding((int)GetOEMCP());
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException)
        {
            return Encoding.Default;
        }
    }
}
