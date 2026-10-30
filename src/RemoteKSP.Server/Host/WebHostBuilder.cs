using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Hosting;
using RemoteKSP.Server.Core;
using RemoteKSP.Server.Services;

namespace RemoteKSP.Server.Host;

public static class WebHostBuilder
{
    public static WebApplication CreateWebHost(
        ClientStore clientStore,
        PairingManager pairingManager,
        KeySourceManager keySources,
        ServerConfig config,
        ActivityLogService activityLog)
    {
        var builder = WebApplication.CreateBuilder();

        // 确保 TLS 证书存在
        var tlsCert = TlsCertificateManager.EnsureCertificate();

        builder.WebHost.UseKestrel(options =>
        {
            options.ListenAnyIP(config.Port, listenOptions =>
            {
                // 每次握手取当前证书：设置里重新生成 TLS 证书后，新连接立即使用新证书，不用重启服务
                listenOptions.UseHttps(https =>
                    https.ServerCertificateSelector = (_, _) => TlsCertificateManager.Current ?? tlsCert);
            });
        });

        var app = builder.Build();
        app.MapSigningEndpoints(clientStore, pairingManager, keySources, activityLog);
        return app;
    }
}
