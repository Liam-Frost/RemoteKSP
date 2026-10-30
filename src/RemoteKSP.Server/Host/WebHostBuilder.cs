using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Hosting;
using RemoteKSP.Server.Core;
using RemoteKSP.Server.Services;

namespace RemoteKSP.Server.Host;

public static class WebHostBuilder
{
    /// <summary>
    /// Messages returned to a client use the language it asks for in Accept-Language (the client GUI sends its
    /// own UI language). Requests without one, such as those of the native KSP, get the service's language.
    /// </summary>
    public static IApplicationBuilder UseRequestLanguage(this IApplicationBuilder app) => app.Use(ApplyRequestLanguage);

    private static Task ApplyRequestLanguage(Microsoft.AspNetCore.Http.HttpContext context, Func<Task> next)
    {
        var requested = context.Request.GetTypedHeaders().AcceptLanguage
            .OrderByDescending(v => v.Quality ?? 1)
            .Select(v => RemoteKSP.Shared.Localization.UiLanguage.Normalize(v.Value.Value))
            .FirstOrDefault(code => code != null);
        if (requested != null)
            System.Globalization.CultureInfo.CurrentUICulture = System.Globalization.CultureInfo.GetCultureInfo(requested);
        return next();
    }

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
        app.UseRequestLanguage();
        app.MapSigningEndpoints(clientStore, pairingManager, keySources, activityLog);
        return app;
    }
}
