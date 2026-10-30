using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Pipes;
using System.Linq;
using System.Security.AccessControl;
using System.Security.Cryptography.X509Certificates;
using System.Security.Principal;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using RemoteKSP.Server.Core;

namespace RemoteKSP.Server.Services;

public class PipeServer : BackgroundService
{
    private const string PipeName = "RemoteKSP_Control";
    private readonly ILogger<PipeServer> _logger;
    private readonly PairingManager _pairingManager;
    private readonly ClientStore _clientStore;
    private readonly KeySourceManager _keySources;
    private readonly ActivityLogService _activityLog;
    private readonly PinAttemptLimiter _pinLimiter;

    public PipeServer(ILogger<PipeServer> logger, PairingManager pairingManager, ClientStore clientStore,
        KeySourceManager keySources, ActivityLogService activityLog, PinAttemptLimiter pinLimiter)
    {
        _logger = logger;
        _pairingManager = pairingManager;
        _clientStore = clientStore;
        _keySources = keySources;
        _activityLog = activityLog;
        _pinLimiter = pinLimiter;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var pipe = CreatePipe();
                await pipe.WaitForConnectionAsync(stoppingToken);
                _ = Task.Run(() => HandleClientAsync(pipe), stoppingToken);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Pipe server error");
                await Task.Delay(1000, stoppingToken);
            }
        }
    }

    private NamedPipeServerStream CreatePipe()
    {
        var security = new PipeSecurity();
        security.AddAccessRule(new PipeAccessRule(
            new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null),
            PipeAccessRights.FullControl, AccessControlType.Allow));
        security.AddAccessRule(new PipeAccessRule(
            new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null),
            PipeAccessRights.FullControl, AccessControlType.Allow));

        return NamedPipeServerStreamAcl.Create(PipeName, PipeDirection.InOut,
            10, PipeTransmissionMode.Byte, PipeOptions.Asynchronous, 4096, 4096, security);
    }

    private async Task HandleClientAsync(NamedPipeServerStream pipe)
    {
        try
        {
            using var reader = new StreamReader(pipe, Encoding.UTF8, leaveOpen: false);
            using var writer = new StreamWriter(pipe, Encoding.UTF8, leaveOpen: false) { AutoFlush = true };

            var requestJson = await reader.ReadLineAsync();
            if (string.IsNullOrEmpty(requestJson))
                return;

            var request = JsonSerializer.Deserialize<PipeRequest>(requestJson);
            if (request == null)
                return;

            var response = await ProcessCommandAsync(request);
            var responseJson = JsonSerializer.Serialize(response);
            await writer.WriteLineAsync(responseJson);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error handling pipe client");
        }
    }

    private Task<PipeResponse> ProcessCommandAsync(PipeRequest request)
    {
        try
        {
            var result = request.Command switch
            {
                "GetStatus" => GetStatus(),
                "GetPendingRequests" => GetPendingRequests(),
                "ListPairingRequests" => GetPendingRequests(), // 添加别名命令
                "ApprovePairing" => ApprovePairing(request.Data),
                "ApprovePairingRequest" => ApprovePairing(request.Data), // 添加别名命令
                "RejectPairing" => RejectPairing(request.Data),
                "RejectPairingRequest" => RejectPairing(request.Data), // 添加别名命令
                "GetClients" => GetClients(),
                "RevokeClient" => RevokeClient(request.Data),
                "SetClientPermissions" => SetClientPermissions(request.Data),
                "GetKeySourceStatus" => _keySources.GetStatus(),
                "GetCertificates" => _keySources.GetCertificateStatuses(),
                "AddCertificate" => AddCertificate(request.Data),
                "UpdateCertificate" => UpdateCertificate(request.Data),
                "RemoveCertificate" => RemoveCertificate(request.Data),
                "SetDefaultCertificate" => SetDefaultCertificate(request.Data),
                "SetCertificatePin" => SetCertificatePin(request.Data),
                "DetectContainers" => DetectContainers(request.Data),
                "ListStoreCertificates" => ListStoreCertificates(request.Data),
                "GenerateTestCert" => GenerateTestCert(),
                "GetTlsCertificate" => GetTlsCertificate(),
                "RegenerateTlsCertificate" => RegenerateTlsCertificate(),
                "ReloadKeySource" => ReloadKeySource(),
                _ => throw new InvalidOperationException($"Unknown command: {request.Command}")
            };

            return Task.FromResult(new PipeResponse { Success = true, Data = JsonSerializer.Serialize(result) });
        }
        catch (Exception ex)
        {
            return Task.FromResult(new PipeResponse { Success = false, Error = ex.Message });
        }
    }

    private object GetStatus() => "Running";

    private object GetPendingRequests()
    {
        var pending = _pairingManager.GetPendingRequests();
        var config = ServerConfig.Load();
        var tlsThumbprint = config.TlsCertThumbprint ?? "";

        return pending.Select(r => new
        {
            r.RequestId,
            r.MachineName,
            r.IpAddress,
            VerificationCode = RemoteKSP.Shared.Protocol.VerificationCode(tlsThumbprint, r.ClientNonce, r.ServerNonce),
            r.CreatedAt,
            r.ExpiresAt
        }).ToArray();
    }

    private object ApprovePairing(string? dataJson)
    {
        if (string.IsNullOrEmpty(dataJson))
            throw new ArgumentException("Missing RequestId");

        var data = JsonSerializer.Deserialize<JsonElement>(dataJson);
        var requestId = data.GetProperty("RequestId").GetString();
        if (string.IsNullOrEmpty(requestId))
            throw new ArgumentException("Invalid RequestId");

        var secret = Guid.NewGuid().ToString("N");
        _pairingManager.Approve(requestId, secret);
        _activityLog.Info("配对", $"批准配对请求 {requestId[..8]}...");
        return new { Success = true };
    }

    private object RejectPairing(string? dataJson)
    {
        if (string.IsNullOrEmpty(dataJson))
            throw new ArgumentException("Missing RequestId");

        var data = JsonSerializer.Deserialize<JsonElement>(dataJson);
        var requestId = data.GetProperty("RequestId").GetString();
        if (string.IsNullOrEmpty(requestId))
            throw new ArgumentException("Invalid RequestId");

        _pairingManager.Reject(requestId);
        _activityLog.Info("配对", $"拒绝配对请求 {requestId[..8]}...");
        return new { Success = true };
    }

    private object GetClients()
    {
        var clients = _clientStore.GetAll();
        return clients.Select(c => new
        {
            c.Id,
            c.MachineName,
            c.IpAddress,
            c.PairedAt,
            c.LastSeen,
            c.AllowedCertIds
        }).ToArray();
    }

    private object RevokeClient(string? dataJson)
    {
        if (string.IsNullOrEmpty(dataJson))
            throw new ArgumentException("Missing ClientId");

        var data = JsonSerializer.Deserialize<JsonElement>(dataJson);
        var clientId = data.GetProperty("ClientId").GetString();
        if (string.IsNullOrEmpty(clientId))
            throw new ArgumentException("Invalid ClientId");

        var client = _clientStore.GetAll().FirstOrDefault(c => c.Id == clientId);
        _clientStore.Revoke(clientId);
        _activityLog.Warning("客户端", $"撤销客户端 {client?.MachineName ?? clientId}");
        return new { Success = true };
    }

    private object SetClientPermissions(string? dataJson)
    {
        if (string.IsNullOrEmpty(dataJson))
            throw new ArgumentException("Missing data");

        var data = JsonSerializer.Deserialize<JsonElement>(dataJson);
        var clientId = data.GetProperty("ClientId").GetString();
        if (string.IsNullOrEmpty(clientId))
            throw new ArgumentException("Invalid ClientId");

        List<string>? allowedCertIds = null;
        if (data.TryGetProperty("AllowedCertIds", out var idsElem) && idsElem.ValueKind == JsonValueKind.Array)
        {
            allowedCertIds = idsElem.EnumerateArray()
                .Select(e => e.GetString())
                .Where(s => s != null)
                .Cast<string>()
                .ToList();
        }

        _clientStore.SetAllowedCertIds(clientId, allowedCertIds);
        return new { Success = true };
    }

    private static JsonElement ParseData(string? dataJson) =>
        string.IsNullOrEmpty(dataJson)
            ? throw new ArgumentException("Missing data")
            : JsonSerializer.Deserialize<JsonElement>(dataJson);

    private static string? GetString(JsonElement data, string name) =>
        data.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    private static string RequireString(JsonElement data, string name) =>
        GetString(data, name) is { Length: > 0 } s ? s : throw new ArgumentException($"缺少 {name}");

    /// <summary>添加证书：先按类型补全指纹并试加载一次，失败不写入注册表</summary>
    private object AddCertificate(string? dataJson)
    {
        var data = ParseData(dataJson);
        var entry = new CertificateEntry
        {
            SourceType = RequireString(data, "SourceType"),
            Name = GetString(data, "Name")?.Trim() ?? "",
            IsShared = !data.TryGetProperty("IsShared", out var shared) || shared.GetBoolean()
        };
        var pin = GetString(data, "Pin");

        switch (entry.SourceType)
        {
            case "test":
                var der = TestKeySource.GenerateCertificateDer(
                    $"RemoteKSP Test Certificate {DateTime.Now:yyyyMMdd-HHmmss}");
                using (var cert = X509CertificateLoader.LoadCertificate(der))
                    entry.Thumbprint = cert.Thumbprint;
                entry.TestCertDer = Convert.ToBase64String(der);
                break;

            case "store":
                entry.StoreLocation = GetString(data, "StoreLocation") ?? "LocalMachine";
                entry.Thumbprint = RequireString(data, "Thumbprint");
                break;

            case "pfx":
                entry.PfxPath = RequireString(data, "PfxPath");
                var password = GetString(data, "PfxPassword") ?? "";
                using (var cert = X509CertificateLoader.LoadPkcs12FromFile(entry.PfxPath, password,
                           X509KeyStorageFlags.EphemeralKeySet))
                    entry.Thumbprint = cert.Thumbprint.ToLowerInvariant();
                entry.PfxPasswordEncrypted = DpapiHelper.ProtectString(password);

                // 数据目录外的 PFX 复制进来：用户之后移动或删除原文件不影响签名
                var fullPath = Path.GetFullPath(entry.PfxPath);
                if (!fullPath.StartsWith(ServerPaths.DataDir + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                {
                    var relative = Path.Combine("pfx", $"{entry.Thumbprint}.pfx");
                    Directory.CreateDirectory(Path.Combine(ServerPaths.DataDir, "pfx"));
                    File.Copy(fullPath, Path.Combine(ServerPaths.DataDir, relative), overwrite: true);
                    entry.PfxPath = relative;
                }
                break;

            case "etoken":
                entry.CspProvider = GetString(data, "CspProvider") ?? "eToken Base Cryptographic Provider";
                entry.KeyContainer = RequireString(data, "KeyContainer");
                entry.Thumbprint = RequireString(data, "Thumbprint");
                break;

            default:
                throw new ArgumentException($"未知的证书类型 {entry.SourceType}");
        }

        entry.Thumbprint = RemoteKSP.Shared.Protocol.NormalizeThumbprint(entry.Thumbprint);
        var source = KeySourceManager.CreateKeySource(entry);

        if (source is ETokenKeySource etoken && !string.IsNullOrEmpty(pin))
        {
            VerifyPinLimited(etoken, entry, pin);
            entry.PinEncrypted = DpapiHelper.ProtectString(pin);
        }

        if (string.IsNullOrEmpty(entry.Name))
        {
            using var cert = X509CertificateLoader.LoadCertificate(source.GetCertificate());
            entry.Name = cert.GetNameInfo(X509NameType.SimpleName, false);
        }

        _keySources.Registry.Add(entry);
        _keySources.Reload();
        _activityLog.Info("证书", $"添加证书：{entry.Name} ({entry.SourceType})", entry.Thumbprint);
        return new { entry.Id, entry.Thumbprint };
    }

    private object UpdateCertificate(string? dataJson)
    {
        var data = ParseData(dataJson);
        var id = RequireString(data, "Id");
        var name = RequireString(data, "Name").Trim();
        var isShared = data.GetProperty("IsShared").GetBoolean();

        var entry = _keySources.Registry.GetById(id) ?? throw new InvalidOperationException("证书不存在");
        _keySources.Registry.Update(id, name, isShared);
        _keySources.Reload();
        _activityLog.Info("证书", $"更新证书：{name} (分享={isShared})");
        return new { Success = true };
    }

    private object RemoveCertificate(string? dataJson)
    {
        var id = RequireString(ParseData(dataJson), "Id");
        var entry = _keySources.Registry.GetById(id);
        _keySources.Registry.Remove(id);
        _keySources.Reload();
        _activityLog.Warning("证书", $"删除证书：{entry?.Name ?? id}");
        return new { Success = true };
    }

    private object SetDefaultCertificate(string? dataJson)
    {
        var id = RequireString(ParseData(dataJson), "Id");
        var entry = _keySources.Registry.GetById(id) ?? throw new InvalidOperationException("证书不存在");
        if (!entry.IsShared)
            throw new InvalidOperationException("未分享的证书不能设为默认证书");

        // 检查证书是否可用
        var status = _keySources.GetCertificateStatuses().FirstOrDefault(s => s.Id == id);
        if (status != null && !status.IsReady)
            throw new InvalidOperationException($"证书不可用：{status.Detail}");

        _keySources.Registry.SetDefault(id);
        _keySources.Reload();
        _activityLog.Info("证书", $"设置默认证书：{entry.Name}");
        return new { Success = true };
    }

    /// <summary>用测试签名验证 eToken PIN，通过后加密保存并立即生效</summary>
    private object SetCertificatePin(string? dataJson)
    {
        var data = ParseData(dataJson);
        var id = RequireString(data, "Id");
        var pin = RequireString(data, "Pin");

        var entry = _keySources.Registry.GetById(id) ?? throw new InvalidOperationException("证书不存在");
        if (entry.SourceType != "etoken")
            throw new InvalidOperationException("只有 eToken 证书需要 PIN");

        var source = (ETokenKeySource)KeySourceManager.CreateKeySource(entry);
        VerifyPinLimited(source, entry, pin);

        var wasDisabled = entry.PinFailure != null;
        _keySources.Registry.SetPin(id, pin);
        _keySources.Reload();
        _activityLog.Info("证书", wasDisabled ? $"设置 PIN：{entry.Name}，已解除停用" : $"设置 PIN：{entry.Name}");
        return new { Success = true };
    }

    /// <summary>
    /// 测试签名验证 PIN。同一个 eToken 容器连续失败 3 次后 1 分钟内拒绝验证、不访问 token，
    /// 防止在 GUI 里反复试错把 token 的重试次数耗光
    /// </summary>
    private void VerifyPinLimited(ETokenKeySource source, CertificateEntry entry, string pin)
    {
        var key = PinAttemptLimiter.KeyFor(entry.CspProvider, entry.KeyContainer);
        if (_pinLimiter.GetLockout(key) is { } wait)
            throw new InvalidOperationException(
                $"PIN 已连续验证失败 {PinAttemptLimiter.MaxFailures} 次，为保护 eToken 已暂停验证，请 {Math.Ceiling(wait.TotalSeconds)} 秒后再试");

        var result = source.VerifyPin(pin);
        if (result.Success)
        {
            _pinLimiter.RecordSuccess(key);
            return;
        }

        var remaining = _pinLimiter.RecordFailure(key);
        var name = string.IsNullOrEmpty(entry.Name) ? entry.KeyContainer : entry.Name;
        _activityLog.Warning("证书", $"PIN 验证失败：{name}", result.Error);

        var reason = result.Failure switch
        {
            PinFailureKind.Locked => "eToken 已锁定（PIN 重试次数已用完），请先用 SafeNet Authentication Client 解锁",
            PinFailureKind.Wrong => "PIN 错误",
            _ => $"测试签名失败：PIN 错误、容器名不对或 eToken 未插入（{result.Error}）"
        };
        var limit = remaining > 0
            ? $"再失败 {remaining} 次将暂停验证 {PinAttemptLimiter.LockoutDuration.TotalMinutes:0} 分钟"
            : $"已连续失败 {PinAttemptLimiter.MaxFailures} 次，请 {PinAttemptLimiter.LockoutDuration.TotalMinutes:0} 分钟后再试";
        throw new InvalidOperationException($"{reason}。{limit}");
    }

    /// <summary>HTTPS 当前使用的 TLS 证书（设置页显示指纹和有效期）</summary>
    private object GetTlsCertificate()
    {
        var cert = TlsCertificateManager.Current;
        return new
        {
            Thumbprint = cert?.Thumbprint.ToLowerInvariant(),
            NotAfter = cert?.NotAfter,
            PairedClients = _clientStore.GetAll().Count
        };
    }

    /// <summary>重新生成 TLS 证书：立即生效，原 server.pfx 备份保留；已配对的客户端都要重新配对</summary>
    private object RegenerateTlsCertificate()
    {
        var result = TlsCertificateManager.RegenerateServerCertificate();
        _activityLog.Warning("系统", "重新生成 TLS 证书，已配对的客户端需要重新配对",
            $"旧指纹 {result.OldThumbprint ?? "（无法读取）"} → 新指纹 {result.NewThumbprint}；原文件备份为 {result.BackupFile ?? "（原来没有文件）"}");
        return result;
    }

    /// <summary>在服务进程（SYSTEM）里枚举 CSP 容器，和签名时看到的一致</summary>
    private object DetectContainers(string? dataJson)
    {
        var csp = GetString(ParseData(dataJson), "CspProvider") ?? "eToken Base Cryptographic Provider";
        return CryptoApi.EnumerateContainers(csp);
    }

    /// <summary>
    /// 列出证书存储里带私钥的证书。由服务枚举：CurrentUser 对服务来说是 SYSTEM 的存储，
    /// GUI 自己枚举看到的是当前登录用户的，会和签名时不一致
    /// </summary>
    private object ListStoreCertificates(string? dataJson)
    {
        var locationName = GetString(ParseData(dataJson), "StoreLocation") ?? "LocalMachine";
        var location = string.Equals(locationName, "CurrentUser", StringComparison.OrdinalIgnoreCase)
            ? StoreLocation.CurrentUser
            : StoreLocation.LocalMachine;

        using var store = new X509Store(StoreName.My, location);
        store.Open(OpenFlags.ReadOnly);

        var allCerts = store.Certificates.Count;
        var certsWithKey = new List<(X509Certificate2 cert, bool hasKey)>();

        // 指向 Remote KSP 的证书（本机客户端同步来的）也算 HasPrivateKey，但不能当本服务端的签名证书
        var remoteKsp = 0;
        foreach (X509Certificate2 cert in store.Certificates)
        {
            var usable = StoreKeySource.IsUsable(cert);
            if (cert.HasPrivateKey && !usable)
                remoteKsp++;
            certsWithKey.Add((cert, usable));
        }

        var withPrivateKey = certsWithKey.Count(c => c.hasKey);

        _activityLog.Info("证书存储", $"枚举 {locationName}\\My",
            $"总数={allCerts}, 可用私钥={withPrivateKey}, 指向 Remote KSP（已排除）={remoteKsp}");

        var result = certsWithKey
            .Where(c => c.hasKey)
            .OrderByDescending(c => c.cert.NotAfter)
            .Select(c => new
            {
                Thumbprint = c.cert.Thumbprint.ToLowerInvariant(),
                Subject = c.cert.GetNameInfo(X509NameType.SimpleName, false),
                Issuer = c.cert.GetNameInfo(X509NameType.SimpleName, true),
                NotAfter = c.cert.NotAfter
            })
            .ToArray();

        return new
        {
            Certificates = result,
            DiagInfo = $"服务进程(SYSTEM)看到: {locationName}\\My 总数={allCerts}, 带私钥={withPrivateKey}" +
                (remoteKsp > 0 ? $"；另有 {remoteKsp} 张私钥指向 Remote KSP（本机客户端同步来的），不能在这里使用" : "")
        };
    }

    private object ReloadKeySource()
    {
        _keySources.Reload();
        return _keySources.GetStatus();
    }

    /// <summary>在服务的数据目录生成测试 PFX（服务以 SYSTEM 运行，GUI 不一定有写权限）</summary>
    private object GenerateTestCert()
    {
        var (pfxPath, password, thumbprint) = TestCertGenerator.Generate();
        return new { PfxFile = pfxPath, Password = password, Thumbprint = thumbprint };
    }
}

public record PipeRequest(string Command, string? Data);
public record PipeResponse
{
    public bool Success { get; set; }
    public string? Data { get; set; }
    public string? Error { get; set; }
}
