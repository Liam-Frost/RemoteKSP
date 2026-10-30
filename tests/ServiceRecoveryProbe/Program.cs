using System.Diagnostics;
using System.ServiceProcess;
using System.Text.RegularExpressions;
using RemoteKSP.Server.Services;

// 开发用探针，必须以管理员运行：
// 1. 以本 exe 的 --svc 模式注册一个临时服务
// 2. 调用生产代码 ServiceRecovery.Configure 写入恢复策略，用 sc qfailure / qfailureflag 读回比对
// 3. 启动服务后强杀进程，确认 SCM 按策略重启
// 4. 删除临时服务
// 用法：ServiceRecoveryProbe.exe <日志文件>

if (args.Contains("--svc"))
{
    ServiceBase.Run(new IdleService());
    return 0;
}

const string Name = "RemoteKSPRecoveryProbe";
using var log = new StreamWriter(args.ElementAtOrDefault(0) ?? "recovery-probe.log") { AutoFlush = true };
var failed = false;
void Line(string s) { log.WriteLine(s); Console.WriteLine(s); }
void Check(bool ok, string what) { Line((ok ? "PASS: " : "FAIL: ") + what); failed |= !ok; }

try
{
    Sc($"delete {Name}");
    Line(Sc($"create {Name} binPath= \"\\\"{Environment.ProcessPath}\\\" --svc\" start= demand"));

    ServiceRecovery.Configure(Name);
    var failure = Sc($"qfailure {Name}");
    var flag = Sc($"qfailureflag {Name}");
    Line(failure + flag);
    Check(failure.Contains("86400"), "reset period is 86400 seconds");
    Check(Regex.IsMatch(failure, @"5000[\s\S]*10000[\s\S]*60000"), "restart actions with 5s / 10s / 60s delays");
    Check(flag.Contains("TRUE"), "FAILURE_ACTIONS_ON_NONCRASH_FAILURES is TRUE");

    using var sc = new ServiceController(Name);
    sc.Start();
    sc.WaitForStatus(ServiceControllerStatus.Running, TimeSpan.FromSeconds(30));
    var pid1 = QueryPid();
    Line($"running, pid {pid1}; killing process");
    Process.GetProcessById(pid1).Kill();

    // 第一次失败的重启延迟是 5 秒，最多等 20 秒
    var pid2 = 0;
    for (var i = 0; i < 40 && (pid2 == 0 || pid2 == pid1); i++)
    {
        Thread.Sleep(500);
        sc.Refresh();
        pid2 = sc.Status == ServiceControllerStatus.Running ? QueryPid() : 0;
    }
    Check(pid2 != 0 && pid2 != pid1, $"SCM restarted the service after a crash (pid {pid1} -> {pid2})");
}
catch (Exception ex)
{
    Line("ERROR: " + ex);
    failed = true;
}
finally
{
    try
    {
        using var sc = new ServiceController(Name);
        if (sc.Status != ServiceControllerStatus.Stopped)
        {
            sc.Stop();
            sc.WaitForStatus(ServiceControllerStatus.Stopped, TimeSpan.FromSeconds(15));
        }
    }
    catch { /* 服务可能不存在或正在重启 */ }
    Line(Sc($"delete {Name}").Trim());
    Line(failed ? "RESULT: FAILED" : "RESULT: ALL PASSED");
}
return failed ? 1 : 0;

static string Sc(string arguments)
{
    using var p = Process.Start(new ProcessStartInfo("sc.exe", arguments)
        { RedirectStandardOutput = true, UseShellExecute = false })!;
    var output = p.StandardOutput.ReadToEnd();
    p.WaitForExit();
    return output;
}

static int QueryPid()
{
    var m = Regex.Match(Sc($"queryex {Name}"), @"PID\s*:\s*(\d+)");
    return m.Success ? int.Parse(m.Groups[1].Value) : 0;
}

/// <summary>什么都不做、只响应 SCM 的服务</summary>
sealed class IdleService : ServiceBase
{
    public IdleService() => ServiceName = "RemoteKSPRecoveryProbe";
}
