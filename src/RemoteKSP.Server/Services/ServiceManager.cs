using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.ServiceProcess;
using System.Text;

namespace RemoteKSP.Server.Services;

/// <summary>SCM 报告的服务状态</summary>
public enum ServiceStatusKind { Unknown, NotInstalled, Stopped, Starting, Running, Stopping, Paused, QueryFailed }

/// <param name="Error">查询失败时的原始错误</param>
public record ServiceState(ServiceStatusKind Kind, string? Error)
{
    public bool IsInstalled => Kind is not (ServiceStatusKind.NotInstalled or ServiceStatusKind.QueryFailed or ServiceStatusKind.Unknown);
    public bool IsRunning => Kind == ServiceStatusKind.Running;
    public bool IsPending => Kind is ServiceStatusKind.Starting or ServiceStatusKind.Stopping;
}

public class ServiceManager
{
    private const string ServiceName = "RemoteKSP";

    public async Task<bool> IsInstalledAsync()
    {
        try { return (await RunHiddenAsync("sc.exe", $"query {ServiceName}")).ExitCode == 0; }
        catch { return false; }
    }

    private const string FirewallRuleName = "RemoteKSP Server";

    /// <summary>
    /// 安装服务：把当前 exe 复制到 %ProgramFiles%\RemoteKSP，用那里的副本注册服务（路径加引号）。
    /// 服务以 SYSTEM 运行，不能直接用下载/发布目录里的 exe（普通用户通常可写）
    /// </summary>
    public async Task InstallAsync()
    {
        await Task.Run(() => ServiceProgram.DeployExecutable(CurrentExecutable(), ServiceProgram.InstalledExe));
        await RunOrThrowAsync("sc.exe",
            $"create {ServiceName} {ServiceProgram.BinPathArgument(ServiceProgram.InstalledExe)} start= auto", Strings.Svc_InstallFailed);

        // 崩溃后自动重启。GUI 和 --install-service 都以管理员运行（app.manifest），可以直接写 SCM。
        // 这里失败不回滚安装：服务每次启动时还会再配置一次（见 SigningHostedService）
        try { ServiceRecovery.Configure(ServiceName); }
        catch (System.ComponentModel.Win32Exception) { }

        // 防火墙规则失败不影响服务安装（可能由组策略管理）
        await UpdateFirewallRuleAsync(Core.ServerConfig.Load().Port);
        RegisterUrlScheme();
    }

    /// <summary>点击配对通知时打开 GUI 的 remoteksp-server: 链接；登记失败只是通知点了没反应，不影响服务</summary>
    private static void RegisterUrlScheme()
    {
        try { UrlScheme.Register(ServiceProgram.InstalledExe); }
        catch (Exception ex) when (ex is UnauthorizedAccessException or System.Security.SecurityException or System.IO.IOException) { }
    }

    /// <summary>
    /// 升级或迁移：停止服务，把当前 exe 部署到 %ProgramFiles%\RemoteKSP，服务改为指向那里，原来在运行就再启动。
    /// 部署失败时服务仍指向原来的程序，并按原状态启动
    /// </summary>
    public async Task UpdateProgramAsync()
    {
        var wasRunning = (await GetStateAsync()).Kind is ServiceStatusKind.Running or ServiceStatusKind.Starting;
        await StopAndWaitAsync();
        try
        {
            await Task.Run(() => ServiceProgram.DeployExecutable(CurrentExecutable(), ServiceProgram.InstalledExe));
            await RunOrThrowAsync("sc.exe",
                $"config {ServiceName} {ServiceProgram.BinPathArgument(ServiceProgram.InstalledExe)}", Strings.Svc_UpdatePathFailed);
        }
        catch when (wasRunning)
        {
            try { await StartAndWaitAsync(); } catch { /* 保留部署失败的原始错误 */ }
            throw;
        }

        try { ServiceRecovery.Configure(ServiceName); }
        catch (System.ComponentModel.Win32Exception) { }
        RegisterUrlScheme();
        if (wasRunning)
            await StartAndWaitAsync();
    }

    /// <param name="ProgramKeptAt">当前 GUI 正从安装目录运行，exe 删不掉：关闭 GUI 后手动删除它</param>
    public record UninstallResult(string? ProgramKeptAt, bool DataRemoved);

    /// <summary>
    /// 卸载服务：停止并删除服务、删除防火墙规则、删除 %ProgramFiles%\RemoteKSP 里的服务程序；
    /// <paramref name="removeData"/> 为 true 时再删除数据目录（证书配置、PIN、客户端配对、TLS 证书）
    /// </summary>
    public async Task<UninstallResult> UninstallAsync(bool removeData = false, bool removeProgram = true)
    {
        // 先停止服务：运行中的服务被 delete 只会「标记为删除」，紧接着 create 会失败；数据目录也还被它占用
        try { await StopAndWaitAsync(); }
        catch { /* 停止失败不影响卸载，继续尝试删除 */ }

        await RunOrThrowAsync("sc.exe", $"delete {ServiceName}", Strings.Svc_UninstallFailed);
        await RunHiddenAsync("netsh", $"advfirewall firewall delete rule name=\"{FirewallRuleName}\"");
        try { UrlScheme.Unregister(); }
        catch (Exception ex) when (ex is UnauthorizedAccessException or System.Security.SecurityException or System.IO.IOException) { }

        string? kept = null;
        if (removeProgram)
            kept = await Task.Run(() => ServiceProgram.RemoveInstalledProgram(ServiceProgram.InstallDir, Environment.ProcessPath));
        if (removeData)
            await Task.Run(() => ServiceProgram.RemoveDataDirectory(Core.ServerPaths.DataDir));
        return new UninstallResult(kept, removeData);
    }

    /// <summary>服务当前注册的 exe 路径（读 ImagePath）；未安装返回 null</summary>
    public static string? RegisteredExecutable()
    {
        using var key = Microsoft.Win32.Registry.LocalMachine.OpenSubKey($@"SYSTEM\CurrentControlSet\Services\{ServiceName}");
        return ServiceProgram.ExeFromImagePath(key?.GetValue("ImagePath", null,
            Microsoft.Win32.RegistryValueOptions.DoNotExpandEnvironmentNames) as string);
    }

    /// <summary>
    /// 当前运行的 exe。只有发布的单文件 exe 能单独复制过去运行；
    /// 开发时 bin 目录里的 exe 旁边还有 RemoteKSP.Server.dll 等依赖，复制过去起不来
    /// </summary>
    private static string CurrentExecutable()
    {
        var exe = Environment.ProcessPath ?? throw new InvalidOperationException(Strings.Svc_NoProgramPath);
        if (File.Exists(Path.Combine(AppContext.BaseDirectory, "RemoteKSP.Server.dll")))
            throw new InvalidOperationException(Strings.Svc_DevBuild);
        return exe;
    }

    /// <summary>修改 HTTPS 端口：写 config.json，更新防火墙规则，服务在运行就重启使新端口生效</summary>
    /// <returns>防火墙规则是否更新成功</returns>
    public async Task<bool> ChangePortAsync(int port)
    {
        if (port is < 1 or > 65535)
            throw new ArgumentOutOfRangeException(nameof(port), Strings.Svc_PortRange);

        var wasRunning = (await GetStateAsync()).Kind is ServiceStatusKind.Running or ServiceStatusKind.Starting;
        // 先停止：服务启动时会写 config.json（补 TLS 指纹），避免和这里同时写
        await StopAndWaitAsync();

        var config = Core.ServerConfig.Load();
        config.Port = port;
        config.Save();
        var firewallOk = (await GetStateAsync()).IsInstalled ? await UpdateFirewallRuleAsync(port) : true;

        if (wasRunning)
            await StartAndWaitAsync();
        return firewallOk;
    }

    /// <summary>本机是否已有程序在监听这个 TCP 端口</summary>
    public static bool IsPortInUse(int port) =>
        System.Net.NetworkInformation.IPGlobalProperties.GetIPGlobalProperties()
            .GetActiveTcpListeners().Any(e => e.Port == port);

    /// <summary>删除旧规则（可能是旧端口）再按当前端口添加；返回是否添加成功</summary>
    private static async Task<bool> UpdateFirewallRuleAsync(int port)
    {
        await RunHiddenAsync("netsh", $"advfirewall firewall delete rule name=\"{FirewallRuleName}\"");
        var (exitCode, _) = await RunHiddenAsync("netsh",
            $"advfirewall firewall add rule name=\"{FirewallRuleName}\" dir=in action=allow protocol=TCP localport={port}");
        return exitCode == 0;
    }

    private async Task StopAndWaitAsync()
    {
        var state = await GetStateAsync();
        if (!state.IsInstalled || state.Kind == ServiceStatusKind.Stopped)
            return;
        await Task.Run(() =>
        {
            using var sc = new ServiceController(ServiceName);
            if (sc.Status == ServiceControllerStatus.StartPending)
                sc.WaitForStatus(ServiceControllerStatus.Running, TimeSpan.FromSeconds(30));
            try
            {
                if (sc.Status is not (ServiceControllerStatus.Stopped or ServiceControllerStatus.StopPending))
                    sc.Stop();
            }
            catch (InvalidOperationException)
            {
                // 刚好自己停了（例如启动失败退出），下面照常等待 Stopped
            }
            sc.WaitForStatus(ServiceControllerStatus.Stopped, TimeSpan.FromSeconds(30));
        });
    }

    private static Task StartAndWaitAsync() => Task.Run(() =>
    {
        using var sc = new ServiceController(ServiceName);
        sc.Start();
        sc.WaitForStatus(ServiceControllerStatus.Running, TimeSpan.FromSeconds(30));
    });

    /// <summary>
    /// 从 Service Control Manager 读取服务的真实状态。只代表 SCM 看到的进程状态：
    /// 运行中不等于 HTTPS 通道或密钥源已就绪
    /// </summary>
    public Task<ServiceState> GetStateAsync() => Task.Run(() =>
    {
        var services = Array.Empty<ServiceController>();
        try
        {
            // GetServices 返回的每个 ServiceController 都持有 SCM 句柄，全部释放（见 finally）
            services = ServiceController.GetServices();
            var service = services.FirstOrDefault(s => s.ServiceName == ServiceName);
            if (service == null)
                return new ServiceState(ServiceStatusKind.NotInstalled, null);
            return new ServiceState(service.Status switch
            {
                ServiceControllerStatus.Running => ServiceStatusKind.Running,
                ServiceControllerStatus.Stopped => ServiceStatusKind.Stopped,
                ServiceControllerStatus.StartPending or ServiceControllerStatus.ContinuePending => ServiceStatusKind.Starting,
                ServiceControllerStatus.StopPending or ServiceControllerStatus.PausePending => ServiceStatusKind.Stopping,
                ServiceControllerStatus.Paused => ServiceStatusKind.Paused,
                _ => ServiceStatusKind.Unknown
            }, null);
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            return new ServiceState(ServiceStatusKind.QueryFailed, ex.Message);
        }
        finally
        {
            foreach (var s in services)
                s.Dispose();
        }
    });

    public Task<bool> IsRunningAsync()
    {
        try
        {
            var service = ServiceController.GetServices()
                .FirstOrDefault(s => s.ServiceName == ServiceName);
            return Task.FromResult(service?.Status == ServiceControllerStatus.Running);
        }
        catch
        {
            return Task.FromResult(false);
        }
    }

    public Task StartAsync() => RunOrThrowAsync("sc.exe", $"start {ServiceName}", Strings.Svc_StartFailed);

    public Task StopAsync() => RunOrThrowAsync("sc.exe", $"stop {ServiceName}", Strings.Svc_StopFailed);

    /// <summary>运行命令行工具，失败时把工具自己的输出（如「指定的服务已存在」）带进异常消息</summary>
    private static async Task RunOrThrowAsync(string fileName, string arguments, string failureMessage)
    {
        var (exitCode, output) = await RunHiddenAsync(fileName, arguments);
        if (exitCode != 0)
            throw new InvalidOperationException(string.Format(Strings.Svc_ToolFailed, failureMessage, fileName, exitCode, output).TrimEnd());
    }

    /// <summary>
    /// 不显示控制台窗口地运行 sc.exe / netsh。GUI 已通过 app.manifest 以管理员运行，子进程继承管理员令牌，
    /// 不需要 Verb=runas（用 runas 必须 UseShellExecute=true，会弹出控制台窗口）
    /// </summary>
    private static async Task<(int ExitCode, string Output)> RunHiddenAsync(string fileName, string arguments)
    {
        var encoding = ConsoleEncoding();
        using var process = Process.Start(new ProcessStartInfo(fileName, arguments)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = encoding,
            StandardErrorEncoding = encoding
        }) ?? throw new InvalidOperationException(string.Format(Strings.Svc_CannotStart, fileName));

        // 两个管道同时读，避免输出较多时互相阻塞
        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();
        return (process.ExitCode, ((await stdout) + (await stderr)).Trim());
    }

    [DllImport("kernel32.dll")]
    private static extern uint GetOEMCP();

    /// <summary>sc.exe 等按 OEM 代码页输出（中文系统可能是 936），不按此解码中文报错会乱码</summary>
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
