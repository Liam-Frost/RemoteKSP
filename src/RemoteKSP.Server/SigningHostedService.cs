using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using RemoteKSP.Server.Core;
using RemoteKSP.Server.Host;
using RemoteKSP.Server.Services;

namespace RemoteKSP.Server;

public class SigningHostedService : BackgroundService
{
    private readonly ILogger<SigningHostedService> _logger;
    private readonly IServiceProvider _services;
    private WebApplication? _webApp;

    public SigningHostedService(ILogger<SigningHostedService> logger, IServiceProvider services)
    {
        _logger = logger;
        _services = services;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("RemoteKSP Signing Service starting");

        // 升级前安装的服务没有恢复策略，启动时补上；SYSTEM 账户有权修改自身配置
        try
        {
            ServiceRecovery.Configure("RemoteKSP");
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to configure service recovery");
        }

        try
        {
            var clientStore = _services.GetRequiredService<ClientStore>();
            var pairingManager = _services.GetRequiredService<PairingManager>();
            var keySources = _services.GetRequiredService<KeySourceManager>();
            var activityLog = _services.GetRequiredService<ActivityLogService>();
            var config = ServerConfig.Load();

            _webApp = WebHostBuilder.CreateWebHost(clientStore, pairingManager, keySources, config, activityLog);
            await _webApp.StartAsync(stoppingToken);
            _logger.LogInformation("HTTPS server started on port {Port}", config.Port);
            activityLog.Info(Strings.LogCat_System, string.Format(Strings.Log_HttpsStarted, config.Port));

            await Task.Delay(Timeout.Infinite, stoppingToken);
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // 正常停止
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Service failed");
            // 非零退出码让 SCM 判定为失败并按恢复策略重启（需要 FailureActionsOnNonCrashFailures）。
            // 只重新抛出时，Host 会正常停止并以 0 退出，SCM 认为是主动停止，不会重启
            Environment.ExitCode = 1;
            throw;
        }
    }

    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        _logger.LogInformation("RemoteKSP Signing Service stopping");

        if (_webApp != null)
        {
            await _webApp.StopAsync(cancellationToken);
            await _webApp.DisposeAsync();
        }

        await base.StopAsync(cancellationToken);
    }
}
