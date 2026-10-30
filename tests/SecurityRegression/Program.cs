using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Security.AccessControl;
using System.Security.Cryptography.X509Certificates;
using System.Security.Principal;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using RemoteKSP.Client.Core;
using RemoteKSP.Client.Services;
using RemoteKSP.Server.Core;
using RemoteKSP.Server.Host;
using RemoteKSP.Server.Services;
using RemoteKSP.Shared;
using RemoteKSP.Shared.Localization;
using RemoteKSP.Shared.Networking;

// Expected texts below are English; the developer's own language preference must not change them
UiLanguage.Apply(UiLanguage.English);

var passed = 0;
void Check(bool condition, string description)
{
    if (!condition) throw new Exception("FAIL: " + description);
    Console.WriteLine("PASS: " + description);
    passed++;
}
void MustThrow(Action action, string description)
{
    try { action(); }
    catch (InvalidOperationException) { Check(true, description); return; }
    throw new Exception("FAIL: " + description);
}

var clock = new TestClock();
var auth = new RequestAuth(clock);
var timestamp = clock.GetUtcNow().ToUnixTimeSeconds().ToString();
var nonce = new string('a', 32);
string Sign(string ts, string n, string client = "client", string body = "body") =>
    Protocol.ComputeSignature("secret", Protocol.Canonical("POST", "/sign/key", ts, "SHA256", client, body, n));
bool Verify(RequestAuth verifier, string ts, string n, string? signature = null, string client = "client") =>
    verifier.VerifySignature("POST", "/sign/key", ts, "SHA256", client, "body", n, signature ?? Sign(ts, n, client), "secret");
Check(Verify(auth, timestamp, nonce), "valid HMAC request accepted");
Check(!Verify(auth, timestamp, nonce), "identical replay rejected");
Check(Verify(auth, timestamp, new string('b', 32)), "identical operation with fresh nonce accepted in same second");
Check(Verify(auth, timestamp, nonce, client: "other"), "nonce scoped to authenticated client");
var forgedNonce = new string('c', 32);
Check(!Verify(auth, timestamp, forgedNonce, Sign(timestamp, nonce)), "tampered nonce rejected");
Check(Verify(auth, timestamp, forgedNonce), "invalid HMAC cannot poison nonce cache");
Check(!Verify(auth, timestamp, ""), "missing nonce rejected");
Check(!Verify(auth, timestamp, new string('A', 32)), "noncanonical nonce rejected");
Check(!Verify(auth, long.MinValue.ToString(), nonce), "extreme timestamp rejected without overflow");
Check(!Verify(auth, (clock.GetUtcNow().ToUnixTimeSeconds() + 301).ToString(), nonce), "future timestamp rejected");
Check(!Verify(auth, (clock.GetUtcNow().ToUnixTimeSeconds() - 301).ToString(), nonce), "expired timestamp rejected");
var concurrent = new RequestAuth(clock);
var winners = 0;
Parallel.For(0, 64, _ => { if (Verify(concurrent, timestamp, nonce)) Interlocked.Increment(ref winners); });
Check(winners == 1, "64 concurrent replays have exactly one winner");
var bounded = new RequestAuth(clock, capacity: 1);
var future = (clock.GetUtcNow().ToUnixTimeSeconds() + 300).ToString();
Check(Verify(bounded, future, nonce), "future boundary accepted");
Check(!Verify(bounded, timestamp, new string('d', 32)), "full cache fails closed");
clock.Advance(TimeSpan.FromSeconds(600));
Check(!Verify(bounded, future, nonce), "nonce retained through full future timestamp window");
clock.Advance(TimeSpan.FromSeconds(1));
Check(Verify(bounded, clock.GetUtcNow().ToUnixTimeSeconds().ToString(), new string('d', 32)), "expired cache entry reclaimed");

var pairing = new PairingManager(clock);
var expired = pairing.CreateRequest("host", "127.0.0.1", "nonce");
clock.Advance(TimeSpan.FromMinutes(5));
MustThrow(() => pairing.Approve(expired.RequestId, "secret"), "expired pending request cannot be approved without cleanup");
Check(pairing.GetRequest(expired.RequestId)?.Status == PairingStatus.Expired, "lookup marks expiry at exact deadline");
var approved = pairing.CreateRequest("host", "127.0.0.1", "nonce");
pairing.Approve(approved.RequestId, "secret");
Check(pairing.GetRequest(approved.RequestId)?.GeneratedSecret == null, "status query never exposes pairing secret");
clock.Advance(TimeSpan.FromMinutes(5));
Check(pairing.ClaimApprovedRequest(approved.RequestId, _ => throw new Exception()) == null,
    "expired approved credentials cannot be claimed");
var single = pairing.CreateRequest("host", "127.0.0.1", "nonce");
pairing.Approve(single.RequestId, "secret");
var registrations = 0;
var claims = 0;
Parallel.For(0, 64, _ =>
{
    var claim = pairing.ClaimApprovedRequest(single.RequestId, _ => Interlocked.Increment(ref registrations));
    if (claim != null) Interlocked.Increment(ref claims);
});
Check(claims == 1 && registrations == 1, "64 concurrent polls register and disclose credentials exactly once");
Check(pairing.GetRequest(single.RequestId)?.Status == PairingStatus.Consumed &&
    pairing.GetRequest(single.RequestId)?.GeneratedSecret == null, "consumed request retains no secret");
var retry = pairing.CreateRequest("host", "127.0.0.1", "nonce");
pairing.Approve(retry.RequestId, "secret");
MustThrow(() => pairing.ClaimApprovedRequest(retry.RequestId, _ => throw new InvalidOperationException()),
    "registration failure reported");
Check(pairing.ClaimApprovedRequest(retry.RequestId, _ => { })?.Secret == "secret", "failed registration leaves claim retryable");
var isolated = pairing.CreateRequest("host", "127.0.0.1", "nonce");
isolated.Status = PairingStatus.Approved;
isolated.GeneratedSecret = "injected";
Check(pairing.ClaimApprovedRequest(isolated.RequestId, _ => { }) == null, "returned snapshot cannot mutate live pairing state");
MustThrow(() => new ServerApi("https://localhost", "client", "secret"), "old unpinned authenticated client requires re-pairing");

// Real HTTPS integration: both native architectures use production http.cpp.
using var rsa = RSA.Create(2048);
var certRequest = new CertificateRequest("CN=localhost", rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
using var generated = certRequest.CreateSelfSigned(DateTimeOffset.UtcNow.AddMinutes(-1), DateTimeOffset.UtcNow.AddHours(1));
// Windows Schannel cannot serve a TLS certificate backed by an ephemeral key.
// The imported temporary key is deleted when the certificate is disposed.
using var certificate = X509CertificateLoader.LoadPkcs12(generated.Export(X509ContentType.Pfx), null,
    X509KeyStorageFlags.Exportable);
var builder = WebApplication.CreateBuilder();
builder.Logging.ClearProviders();
builder.WebHost.ConfigureKestrel(k => k.Listen(IPAddress.Loopback, 0, o => o.UseHttps(certificate)));
await using var app = builder.Build();
var received = 0;
// 非 null 时 /sign 返回 503；非空字符串同时作为 X-RemoteKSP-Error 头
string? signError = null;
var integrationAuth = new RequestAuth();
app.Map("/cert/{thumbprint}", HandleAuthenticated);
app.Map("/sign/{thumbprint}", HandleAuthenticated);
app.MapGet("/auth/check", HandleAuthenticated);
app.MapGet("/redirect", () => Results.Redirect("https://127.0.0.1:1/leak"));
app.MapPost("/pair", () => Results.Ok(new { requestId = "request", serverNonce = "nonce", expiresAt = DateTime.UtcNow.AddMinutes(5) }));
app.MapGet("/pair/{id}", () => Results.Ok(new { status = "consumed" }));
async Task HandleAuthenticated(HttpContext context)
{
    Interlocked.Increment(ref received);
    var req = context.Request;
    var body = await new StreamReader(req.Body).ReadToEndAsync();
    var header = req.Headers.Authorization.ToString();
    var valid = header.StartsWith(Protocol.AuthScheme) && integrationAuth.VerifySignature(req.Method, req.Path,
        req.Headers[Protocol.HeaderTimestamp].ToString(), req.Headers[Protocol.HeaderHashAlgorithm].ToString(),
        req.Headers[Protocol.HeaderClientId].ToString(), body, req.Headers[Protocol.HeaderNonce].ToString(),
        header[Protocol.AuthScheme.Length..], "synthetic-regression-secret");
    if (valid && signError != null && req.Path.StartsWithSegments("/sign"))
    {
        context.Response.StatusCode = 503;
        if (signError.Length > 0) context.Response.Headers[Protocol.HeaderError] = signError;
        return;
    }
    context.Response.StatusCode = valid ? 200 : 401;
    if (valid) await context.Response.Body.WriteAsync(new byte[] { 1, 2, 3 });
}
await app.StartAsync();
var address = app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.Single();
Console.WriteLine("HTTPS test endpoint: " + address);
using (var baseline = new ServerApi(address, "regression-client", "synthetic-regression-secret", certificate.Thumbprint))
{
    using var response = await baseline.SendAsync(HttpMethod.Get, "/auth/check");
    Check(response.IsSuccessStatusCode, "HTTPS test server reachable from managed client");
}
var root = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../../"));
foreach (var arch in new[] { "x64", "x86" })
{
    var probe = Path.Combine(root, "tests", "native", $"HttpPinProbe_{arch}.exe");
    if (!File.Exists(probe)) throw new FileNotFoundException("Run tests/native/build.cmd first", probe);
    async Task<int> RunProbe(string url, string pin, string operation, string? expectStatus = null)
    {
        var info = new ProcessStartInfo(probe) { RedirectStandardOutput = true, RedirectStandardError = true };
        info.ArgumentList.Add(url); info.ArgumentList.Add(pin); info.ArgumentList.Add(operation);
        if (expectStatus != null) info.ArgumentList.Add(expectStatus);
        using var process = Process.Start(info)!;
        var output = process.StandardOutput.ReadToEndAsync();
        var error = process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(40));
        Console.WriteLine($"  {arch} {operation}: {await output} {await error}".TrimEnd());
        return process.ExitCode;
    }
    Check(await RunProbe(address, certificate.Thumbprint, "cert") == 0, $"{arch}: self-signed pinned TLS and native HMAC interoperate");
    Check(await RunProbe(address, certificate.Thumbprint, "sign") == 0, $"{arch}: native signing request accepted");
    var before = received;
    Check(await RunProbe(address, new string('0', 40), "sign") != 0 && received == before,
        $"{arch}: replaced TLS certificate rejected before HTTP credentials/body reach server");
    Check(await RunProbe(address, "", "cert") != 0 && received == before, $"{arch}: missing pin fails closed");
    Check(await RunProbe(address.Replace("https:", "http:"), certificate.Thumbprint, "sign") != 0 && received == before,
        $"{arch}: plaintext HTTP rejected");
    // 服务端证书因 PIN 停用：X-RemoteKSP-Error 映射成智能卡错误码，普通 503 仍是 NTE_DEVICE_NOT_READY
    signError = Protocol.ErrorPinWrong;
    Check(await RunProbe(address, certificate.Thumbprint, "sign", "8010006B") == 0, $"{arch}: pin-wrong maps to SCARD_W_WRONG_CHV");
    signError = Protocol.ErrorPinLocked;
    Check(await RunProbe(address, certificate.Thumbprint, "sign", "8010006C") == 0, $"{arch}: pin-locked maps to SCARD_W_CHV_BLOCKED");
    signError = "";
    Check(await RunProbe(address, certificate.Thumbprint, "sign", "80090030") == 0, $"{arch}: plain 503 stays NTE_DEVICE_NOT_READY");
    signError = null;
}
using (var api = new ServerApi(address, "regression-client", "synthetic-regression-secret", certificate.Thumbprint))
{
    using var first = await api.SendAsync(HttpMethod.Get, "/auth/check");
    using var second = await api.SendAsync(HttpMethod.Get, "/auth/check");
    Check(first.IsSuccessStatusCode && second.IsSuccessStatusCode, "C# generates independent authenticated nonces");
    using var redirect = await api.SendAsync(HttpMethod.Get, "/redirect");
    Check(redirect.StatusCode == HttpStatusCode.Redirect, "C# authenticated requests do not follow redirects");
}
using (var wrong = new ServerApi(address, "regression-client", "synthetic-regression-secret", new string('0', 40)))
{
    var before = received;
    var rejected = false;
    try { using var response = await wrong.SendAsync(HttpMethod.Get, "/auth/check"); }
    catch (Exception ex) when (ex is HttpRequestException || ex is InvalidOperationException) { rejected = true; }
    Check(rejected && received == before, "C# wrong TLS pin rejected before authenticated HTTP request");
}
var pairClient = new PairingClient();
var start = await pairClient.StartPairingAsync(address, "test-machine");
var consumedRejected = false;
try { await pairClient.CheckPairingStatusAsync(address, start.RequestId); }
catch (InvalidOperationException) { consumedRejected = true; }
Check(consumedRejected, "pairing client reports consumed credentials instead of polling indefinitely");
await app.StopAsync();

// Exercise the production pairing/authentication routes with an isolated data folder.
var dataDir = Path.Combine(AppContext.BaseDirectory, "test-data", Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(dataDir);
var routeClock = new TestClock();
var routePairing = new PairingManager(routeClock);
var clients = new ClientStore(dataDir);
var routeBuilder = WebApplication.CreateBuilder();
routeBuilder.Logging.ClearProviders();
routeBuilder.WebHost.ConfigureKestrel(k => k.Listen(IPAddress.Loopback, 0, o => o.UseHttps(certificate)));
await using var routes = routeBuilder.Build();
routes.MapSigningEndpoints(clients, routePairing, new KeySourceManager(new CertificateRegistry(dataDir)), new ActivityLogService());
await routes.StartAsync();
var routeAddress = routes.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.Single();
using var routeHttp = new HttpClient(new HttpClientHandler
{
    ServerCertificateCustomValidationCallback = (_, cert, _, _) => cert?.Thumbprint == certificate.Thumbprint
}) { BaseAddress = new Uri(routeAddress) };
using var pairResponse = await routeHttp.PostAsJsonAsync("/pair", new { MachineName = "integration-host", ClientNonce = "nonce" });
var pairJson = await pairResponse.Content.ReadFromJsonAsync<JsonElement>();
var requestId = pairJson.GetProperty("requestId").GetString()!;
routePairing.Approve(requestId, "synthetic-route-secret");
var polls = await Task.WhenAll(Enumerable.Range(0, 32).Select(async _ =>
{
    using var response = await routeHttp.GetAsync($"/pair/{requestId}");
    response.EnsureSuccessStatusCode();
    return await response.Content.ReadFromJsonAsync<JsonElement>();
}));
var disclosed = polls.Where(p => p.TryGetProperty("secret", out _)).ToArray();
Check(disclosed.Length == 1 && clients.GetAll().Count == 1,
    "production HTTP route: concurrent polling discloses one secret and registers one client");
Check(polls.Count(p => p.GetProperty("status").GetString() == "consumed") == 31,
    "production HTTP route: subsequent polls report consumed without credentials");
var routeClientId = disclosed.Single().GetProperty("clientId").GetString()!;
using (var api = new ServerApi(routeAddress, routeClientId, "synthetic-route-secret", certificate.Thumbprint))
{
    using var response = await api.SendAsync(HttpMethod.Get, "/auth/check");
    Check(response.IsSuccessStatusCode, "production authentication route accepts C# nonce/HMAC protocol");
}
var routeTimestamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds().ToString();
var routeNonce = new string('e', 32);
var routeSignature = Protocol.ComputeSignature("synthetic-route-secret",
    Protocol.Canonical("GET", "/auth/check", routeTimestamp, "SHA256", routeClientId, "", routeNonce));
async Task<HttpStatusCode> ReplayRoute(string scheme, bool includeNonce)
{
    using var request = new HttpRequestMessage(HttpMethod.Get, "/auth/check");
    request.Headers.Add(Protocol.HeaderClientId, routeClientId);
    request.Headers.Add(Protocol.HeaderTimestamp, routeTimestamp);
    request.Headers.Add(Protocol.HeaderHashAlgorithm, "SHA256");
    if (includeNonce) request.Headers.Add(Protocol.HeaderNonce, routeNonce);
    request.Headers.Add("Authorization", scheme + routeSignature);
    using var response = await routeHttp.SendAsync(request);
    return response.StatusCode;
}
Check(await ReplayRoute(Protocol.AuthScheme, false) == HttpStatusCode.Unauthorized, "production route rejects missing nonce");
Check(await ReplayRoute("RKSP-HMAC ", true) == HttpStatusCode.Unauthorized, "production route rejects legacy authentication");
Check(await ReplayRoute(Protocol.AuthScheme, true) == HttpStatusCode.OK &&
    await ReplayRoute(Protocol.AuthScheme, true) == HttpStatusCode.Unauthorized, "production route rejects replay of a valid HTTP request");
using var expiredResponse = await routeHttp.PostAsJsonAsync("/pair", new { MachineName = "expired-host", ClientNonce = "nonce" });
var expiredJson = await expiredResponse.Content.ReadFromJsonAsync<JsonElement>();
var expiredId = expiredJson.GetProperty("requestId").GetString()!;
routePairing.Approve(expiredId, "expired-secret");
routeClock.Advance(TimeSpan.FromMinutes(5));
var expiryJson = await routeHttp.GetFromJsonAsync<JsonElement>($"/pair/{expiredId}");
Check(expiryJson.GetProperty("status").GetString() == "expired" && !expiryJson.TryGetProperty("secret", out _) &&
    clients.GetAll().Count == 1, "production route refuses expired approved credentials without background cleanup");
await routes.StopAsync();
Console.WriteLine($"All {passed} security regression checks passed.");

// Feature tests: SignTool command builder and network address discovery
Console.WriteLine("\n--- Feature Tests: SignTool Command Builder ---");
Check(SignToolCommandBuilder.NormalizeThumbprint("AB:CD:EF:12") == null, "short thumbprint rejected");
Check(SignToolCommandBuilder.NormalizeThumbprint("not-hex-chars!") == null, "non-hex thumbprint rejected");
Check(SignToolCommandBuilder.NormalizeThumbprint("ab cd ef 12 34 56 78 90 ab cd ef 12 34 56 78 90 ab cd ef 12")
    == "ABCDEF1234567890ABCDEF1234567890ABCDEF12", "thumbprint normalized to uppercase without spaces");
Check(SignToolCommandBuilder.IsAbsoluteWindowsPath(@"C:\test.exe"), "C: path is absolute");
Check(SignToolCommandBuilder.IsAbsoluteWindowsPath(@"\\server\share\file.exe"), "UNC path is absolute");
Check(!SignToolCommandBuilder.IsAbsoluteWindowsPath(@"relative\path.exe"), "relative path rejected");
Check(!SignToolCommandBuilder.IsAbsoluteWindowsPath(@"/unix/path"), "Unix path rejected");

var localMachineInput = new SignToolCommandInput
{
    Mode = SignToolMode.Discovered,
    ToolPath = @"C:\SDK\signtool.exe",
    Thumbprint = "a1b2c3d4e5f60718293a4b5c6d7e8f9012345678",
    StoreLocation = StoreLocation.LocalMachine,
    StoreName = "My",
    TargetFile = @"C:\build\app.exe",
    UseTimestamp = true,
    TimestampUrl = "http://timestamp.digicert.com",
    Verbose = false
};
var localResult = SignToolCommandBuilder.Build(localMachineInput);
Check(localResult.IsValid, "valid LocalMachine input builds successfully");
Check(localResult.Command == "\"C:\\SDK\\signtool.exe\" sign /sm /sha1 A1B2C3D4E5F60718293A4B5C6D7E8F9012345678 " +
      "/fd SHA256 /tr \"http://timestamp.digicert.com\" /td SHA256 \"C:\\build\\app.exe\"",
    "LocalMachine command matches expected text exactly");
Check(!SignToolCommandBuilder.Build(localMachineInput with { Thumbprint = "abc123" }).IsValid,
    "short thumbprint blocks command generation");
var spacedResult = SignToolCommandBuilder.Build(localMachineInput with
{
    ToolPath = @"C:\Program Files (x86)\Windows Kits\10\bin\10.0.26100.0\x64\signtool.exe",
    TargetFile = @"D:\构建 输出\我的 应用.exe"
});
Check(spacedResult.IsValid &&
      spacedResult.Command!.StartsWith("\"C:\\Program Files (x86)\\Windows Kits\\10\\bin\\10.0.26100.0\\x64\\signtool.exe\" sign") &&
      spacedResult.Command.EndsWith(" \"D:\\构建 输出\\我的 应用.exe\""),
    "paths with spaces, parentheses and Unicode are quoted as single arguments");
Check(!SignToolCommandBuilder.Build(localMachineInput with { TargetFile = "C:\\a.exe\r\ndel C:\\x" }).IsValid,
    "newline in target path rejected (prevents a second CMD command)");
Check(!SignToolCommandBuilder.Build(localMachineInput with { TimestampUrl = "not a url" }).IsValid &&
      !SignToolCommandBuilder.Build(localMachineInput with { TimestampUrl = "ftp://ts.example.com" }).IsValid,
    "invalid or non-http timestamp URL rejected");
Check(SignToolCommandBuilder.Build(localMachineInput with { Verbose = true }).Command!.Contains(" sign /v /sm "),
    "verbose flag placed after sign subcommand");
Check(!SignToolCommandBuilder.Build(localMachineInput with { TargetFile = "app.exe" }).IsValid,
    "relative target path rejected");
Check(!SignToolCommandBuilder.Build(localMachineInput with { ToolPath = "" }).IsValid &&
      !SignToolCommandBuilder.Build(localMachineInput with { Mode = SignToolMode.Custom, ToolPath = "signtool.exe" }).IsValid,
    "discovered/custom mode requires a full signtool.exe path");
Check(!SignToolCommandBuilder.Build(localMachineInput with { TargetFile = @"C:\a!b.exe" }).IsValid,
    "! rejected (CMD delayed expansion)");
Check(SignToolCommandBuilder.Build(localMachineInput with { StoreName = "Root" }).Command!.Contains(" /sm /s Root /sha1 "),
    "non-My store adds /s <name>");
Check(localResult.Command!.Contains("/sm"), "LocalMachine store uses /sm flag");
Check(localResult.Command.Contains("/sha1"), "command includes certificate thumbprint selector");
Check(localResult.Command.Contains("/fd SHA256"), "command uses SHA256 file digest");
Check(localResult.Command.Contains("/tr") && localResult.Command.Contains("/td SHA256"), "timestamp uses RFC 3161 with SHA256");

var currentUserInput = localMachineInput with { StoreLocation = StoreLocation.CurrentUser };
var currentResult = SignToolCommandBuilder.Build(currentUserInput);
Check(currentResult.IsValid && !currentResult.Command!.Contains("/sm"), "CurrentUser store omits /sm flag");

var pathModeInput = localMachineInput with { Mode = SignToolMode.PathCommand, ToolPath = null };
var pathResult = SignToolCommandBuilder.Build(pathModeInput);
Check(pathResult.IsValid && pathResult.Command!.StartsWith("signtool sign"), "PATH mode uses bare signtool command");

var unsafePathInput = localMachineInput with { ToolPath = @"C:\bad%var%.exe" };
var unsafeResult = SignToolCommandBuilder.Build(unsafePathInput);
Check(!unsafeResult.IsValid && unsafeResult.Errors.ContainsKey(SignToolCommandBuilder.FieldTool),
    "tool path with % character rejected for CMD safety");

var unsafeTargetInput = localMachineInput with { TargetFile = @"C:\file with ""quote"".exe" };
var unsafeTargetResult = SignToolCommandBuilder.Build(unsafeTargetInput);
Check(!unsafeTargetResult.IsValid && unsafeTargetResult.Errors.ContainsKey(SignToolCommandBuilder.FieldTarget),
    "target file with embedded quote rejected");

var trailingBackslashInput = localMachineInput with { TargetFile = @"C:\directory\" };
var trailingResult = SignToolCommandBuilder.Build(trailingBackslashInput);
Check(!trailingResult.IsValid, "path ending with backslash rejected (would escape closing quote)");

var noTimestampInput = localMachineInput with { UseTimestamp = false };
var noTimestampResult = SignToolCommandBuilder.Build(noTimestampInput);
Check(noTimestampResult.IsValid && !noTimestampResult.Command!.Contains("/tr"), "timestamp can be disabled");

Console.WriteLine("\n--- Feature Tests: Network Address Discovery ---");
var netSvc = new NetworkAddressService();
var addresses = netSvc.GetAddresses();
Console.WriteLine($"Discovered {addresses.Count} network addresses on this machine");
Check(addresses.Count >= 0, "network enumeration completes without crash");
var ipv4 = addresses.FirstOrDefault(a => !a.IsIPv6 && !a.IsLoopback);
if (ipv4 != null)
    Console.WriteLine($"  Sample IPv4: {ipv4.InterfaceName} - {ipv4.AddressText}");
var ipv6 = addresses.FirstOrDefault(a => a.IsIPv6 && !a.IsLoopback && !a.IsLinkLocal);
if (ipv6 != null)
    Console.WriteLine($"  Sample IPv6: {ipv6.InterfaceName} - {ipv6.AddressText}");

Check(NetworkAddressInfo.BuildHttpsUrl(IPAddress.Parse("192.168.1.100"), 8443) == "https://192.168.1.100:8443",
    "IPv4 URL formatted correctly");
Check(NetworkAddressInfo.BuildHttpsUrl(IPAddress.Parse("2001:db8::1"), 8443) == "https://[2001:db8::1]:8443",
    "IPv6 URL uses brackets");
var linkLocal = IPAddress.Parse("fe80::1%12");
var linkLocalUrl = NetworkAddressInfo.BuildHttpsUrl(linkLocal, 8443);
Check(linkLocalUrl == "https://[fe80::1%2512]:8443" && Uri.TryCreate(linkLocalUrl, UriKind.Absolute, out _),
    "IPv6 link-local zone ID percent-encoded as %25 and URL parses");
Check(new NetworkAddressInfo { Address = linkLocal }.AddressText == "fe80::1",
    "copied link-local IP omits the machine-local zone ID");
Check(addresses.Select(a => (a.InterfaceId, a.AddressText)).SequenceEqual(
          netSvc.GetAddresses().Select(a => (a.InterfaceId, a.AddressText))),
    "address order is stable across refreshes");
Check(addresses.All(a => a.IsPrimary == (!a.IsLoopback && !a.IsLinkLocal) || !a.IsPrimary),
    "loopback and link-local addresses are never primary");

Console.WriteLine("\n--- Feature Tests: Overview Dashboard ---");
NetworkAddressInfo Nic(string name, string id, string ip, bool primary, bool loopback = false, bool linkLocal = false) => new()
{
    InterfaceId = id, InterfaceName = name, InterfaceDescription = name, Address = IPAddress.Parse(ip),
    IsPrimary = primary, IsLoopback = loopback, IsLinkLocal = linkLocal
};
var ipv4Row = new NetworkAddressItem(Nic("WLAN", "w", "10.0.0.5", true), 8443);
Check(ipv4Row.DetailText == "10.0.0.5", "IPv4 row tooltip does not read the IPv6-only ScopeId");
var linkLocalRow = new NetworkAddressItem(Nic("WLAN", "w", "fe80::1%12", false, linkLocal: true), 8443);
Check(!linkLocalRow.CanCopyServerUrl && linkLocalRow.ServerUrlToolTip.Contains("will not work on other devices"),
    "link-local IPv6 connection URL is not offered for copying, with an explanation");
var loopbackRow = new NetworkAddressItem(Nic("Loopback", "lo", "127.0.0.1", false, loopback: true), 8443);
Check(loopbackRow.CanCopyServerUrl && loopbackRow.ServerUrlToolTip.Contains("only work on this computer") && loopbackRow.MetaText.Contains("this computer only"),
    "loopback URL is copyable and labelled local-only");
Check(new NetworkAddressItem(Nic("WLAN", "w", "10.0.0.5", true), null) is { HasServerUrl: false, CanCopyServerUrl: false },
    "client rows (no port) never produce a server URL");

var netVm = new RemoteKSP.Shared.Networking.NetworkAddressesViewModel(
    new RemoteKSP.Shared.Services.DialogService(new Wpf.Ui.ContentDialogService()), () => 8443, "", "");
netVm.Show([
    Nic("WLAN", "w", "10.0.0.5", true), Nic("VPN", "v", "172.16.0.2", true), Nic("WLAN", "w", "2001:db8::5", true),
    Nic("Loopback", "lo", "127.0.0.1", false, loopback: true), Nic("WLAN", "w", "fe80::1%12", false, linkLocal: true),
    Nic("Loopback", "lo", "::1", false, loopback: true)
]);
Check(netVm.Groups.Select(g => g.Name).SequenceEqual(["WLAN", "VPN"]) && netVm.Groups[0].Addresses.Count == 2,
    "collapsed network list: primary addresses grouped per interface, IPv4 before IPv6");
Check(netVm.HiddenCount == 3 && netVm.ToggleText == "Other addresses (3)", "disclosure counts hidden addresses, not interfaces");
netVm.ShowOther = true;
Check(netVm.Groups.Select(g => g.Name).SequenceEqual(["WLAN", "VPN", "Loopback"]) &&
      netVm.Groups[0].Addresses.Select(a => a.AddressText).SequenceEqual(["10.0.0.5", "2001:db8::5", "fe80::1"]),
    "expanded list keeps each interface once, with its extra addresses under it");
netVm.ShowOther = false;
netVm.Show([Nic("Loopback", "lo", "127.0.0.1", false, loopback: true)]);
Check(!netVm.HasAddresses && netVm.EmptyText.Contains("only loopback or link-local") && netVm.HasHidden,
    "only loopback: explains why nothing is listed and offers the hidden addresses");

Check(RemoteKSP.Shared.Ui.TextSummary.Names(["sign-01", "sign-02"]) == "sign-01, sign-02" &&
      RemoteKSP.Shared.Ui.TextSummary.Names(["a", "b", "c", "d", "e"]) == "a, b, c +2",
    "server name summary caps at three names and shows +N");

KeySourceStatus Ks(string mode, string thumb) => new(mode, thumb, "detail");
Check(RemoteKSP.Server.ViewModels.OverviewViewModel.MapKeySource(Ks("production", "ab")) == RemoteKSP.Server.ViewModels.KeySourceState.Ready &&
      RemoteKSP.Server.ViewModels.OverviewViewModel.MapKeySource(Ks("test", "ab")) == RemoteKSP.Server.ViewModels.KeySourceState.NotReady &&
      RemoteKSP.Server.ViewModels.OverviewViewModel.MapKeySource(Ks("test", "")) == RemoteKSP.Server.ViewModels.KeySourceState.NotConfigured &&
      RemoteKSP.Server.ViewModels.OverviewViewModel.MapKeySource(null) == RemoteKSP.Server.ViewModels.KeySourceState.QueryFailed,
    "key source status maps production/test/no shared certificate/empty reply");

// 服务在运行但管理通道不可用：必须显示「无法获取密钥源状态」，不能说服务未运行
var serverVm = new RemoteKSP.Server.ViewModels.OverviewViewModel(new ServiceManager(), new PipeClient(),
    new RemoteKSP.Shared.Services.DialogService(new Wpf.Ui.ContentDialogService()), _ => { });
var runningState = new ServiceState(ServiceStatusKind.Running, null);
serverVm.Apply(new(runningState, RemoteKSP.Server.ViewModels.KeySourceState.ChannelUnavailable, null, "timeout", null, "timeout"));
Check(serverVm.ServiceValue == "Running" && serverVm.KeySourceValue == "Key source status unavailable" &&
      serverVm.KeySourceDetail.Contains("management channel") && serverVm.KeySourceToolTip == "timeout" && serverVm.ClientsValue == "Unavailable",
    "running service + unreachable pipe: key source and clients say unavailable, raw error kept in tooltip");
Check(serverVm.CanStopService && !serverVm.CanStartService && !serverVm.CanInstallService,
    "running service offers stop only");
serverVm.Apply(new(new(ServiceStatusKind.Stopped, null), RemoteKSP.Server.ViewModels.KeySourceState.ServiceNotRunning, null, null, null, null));
Check(serverVm.CanStartService && !serverVm.CanStopService && serverVm.KeySourceValue == "Service not running" && serverVm.ClientsValue == "—",
    "stopped service offers start; key source says not running only when SCM says so");
serverVm.Apply(new(new(ServiceStatusKind.Starting, null), RemoteKSP.Server.ViewModels.KeySourceState.ServicePending, null, null, null, null));
Check(!serverVm.CanStartService && !serverVm.CanStopService && !serverVm.StartServiceCommand.CanExecute(null),
    "pending service disables start and stop");
serverVm.Apply(new(runningState, RemoteKSP.Server.ViewModels.KeySourceState.Ready, Ks("production", "ab"), null, 0, null));
Check(serverVm.ClientsValue == "No authorized clients" && serverVm.ClientsActionText == "View pairing requests",
    "zero clients is shown as such (not as a failure) and links to pairing requests");
Check(new ServiceState(ServiceStatusKind.QueryFailed, "x") is { IsInstalled: false, IsRunning: false },
    "SCM query failure is not treated as installed or running");

var clientVm = new RemoteKSP.Client.ViewModels.OverviewViewModel(null!, null!, _ => { });
clientVm.Apply(new(RemoteKSP.Client.ViewModels.KspInstallState.NeedsUpdate, null, ["sign-01", "sign-02", "a", "b"], 0, 0));
Check(clientVm.KspInstalled && clientVm.KspNeedsInstall && clientVm.InstallKspText == "Update KSP" &&
      clientVm.ServersDetail == "sign-01, sign-02, a +1" && clientVm.CertificatesValue == "No bound certificates yet",
    "client overview: update offered, names summarised, zero certificates stated");
clientVm.Apply(new(RemoteKSP.Client.ViewModels.KspInstallState.Installed, null, [], 3, 1));
Check(clientVm.ServersValue == "No server paired yet" && clientVm.ServersToolTip == null && clientVm.CertificatesValue == "3 certificate(s)" &&
      clientVm.KspDetail.Contains("run Diagnostics"), "client overview: installed KSP does not claim signing works");

Console.WriteLine("\n--- Feature Tests: Key Source Retry ---");
var retryDir = Path.Combine(Path.GetTempPath(), "rksp-retry-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(retryDir);
try
{
    // PFX 文件一开始不存在，模拟开机时密钥源未就绪；稍后写入文件模拟「设备就绪」
    using var retryRsa = RSA.Create(2048);
    var retryReq = new CertificateRequest("CN=RemoteKSP Retry Probe", retryRsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
    using var retryCert = retryReq.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(1));
    var pfxPath = Path.Combine(retryDir, "late.pfx");
    var retryThumb = retryCert.Thumbprint.ToLowerInvariant();
    // 预先写好 certificates.json，避免 CertificateRegistry 从本机真实 config.json 迁移
    File.WriteAllText(Path.Combine(retryDir, "certificates.json"), JsonSerializer.Serialize(new
    {
        Certificates = new[] { new { Id = "cert-001", Name = "延迟就绪证书", Thumbprint = retryThumb,
            SourceType = "pfx", IsShared = true, AddedAt = DateTime.UtcNow, PfxPath = pfxPath } }
    }));
    KeySourceManager NewManager() => new(new CertificateRegistry(retryDir));
    bool Ready(KeySourceManager m) => m.GetCertificateStatuses().Single().IsReady;

    var mgr = NewManager();
    mgr.ConfigureRetry(3);
    Check(!Ready(mgr), "retry: certificate unavailable while PFX is missing");
    var r1 = mgr.RetryFailed(3);
    Check(r1.Outcome == RetryOutcome.StillFailing && r1.Attempt == 1 && r1.Remaining == 1,
        "retry: first attempt still failing, attempt counted");
    Check(mgr.GetCertificateStatuses().Single().Detail.Contains("retrying automatically 1/3"), "retry: status shows retry progress");
    File.WriteAllBytes(pfxPath, retryCert.Export(X509ContentType.Pfx));
    var r2 = mgr.RetryFailed(3);
    Check(r2.Outcome == RetryOutcome.AllRecovered && r2.Recovered.SequenceEqual(["延迟就绪证书"]) && Ready(mgr),
        "retry: certificate recovers once the key source becomes available");
    Check(mgr.RetryFailed(3).Outcome == RetryOutcome.NothingToRetry, "retry: nothing to retry after recovery");
    Check(mgr.SignHash(retryThumb, SHA256.HashData("x"u8), "SHA256").Length == 256, "retry: recovered certificate signs");

    // 上限：用完之后不再加载；Reload（配置变更）清零
    File.Delete(pfxPath);
    var capped = NewManager();
    capped.ConfigureRetry(2);
    Check(capped.RetryFailed(2).Attempt == 1 && capped.RetryFailed(2).Attempt == 2, "retry: attempts 1 and 2 run");
    var exhausted = capped.RetryFailed(2);
    Check(exhausted.Outcome == RetryOutcome.Exhausted && exhausted.Attempt == 2, "retry: stops at max attempts");
    Check(capped.GetCertificateStatuses().Single().Detail.Contains("retrying stopped"), "retry: status says retrying stopped");
    capped.Reload();
    Check(capped.RetryFailed(2) is { Outcome: RetryOutcome.StillFailing, Attempt: 1 }, "retry: Reload resets the attempt budget");

    // 后台服务：20ms 间隔、上限 3，跑完后应停止并且只记一条错误日志
    var svcMgr = NewManager();
    var svcLog = new ActivityLogService();
    var retrySvc = new KeySourceRetryService(svcMgr, svcLog,
        Microsoft.Extensions.Logging.Abstractions.NullLogger<KeySourceRetryService>.Instance, TimeSpan.FromMilliseconds(20), 3);
    await retrySvc.StartAsync(CancellationToken.None);
    await Task.Delay(600);
    await retrySvc.StopAsync(CancellationToken.None);
    Check(svcMgr.RetryFailed(3).Outcome == RetryOutcome.Exhausted, "retry service: used exactly the configured budget");
    Check(svcLog.GetRecentLogs().Count(l => l.Level == "ERROR") == 1, "retry service: logs give-up once, not every tick");
}
finally
{
    Directory.Delete(retryDir, true);
}

Console.WriteLine("\n--- Feature Tests: eToken PIN Protection ---");
Check(PinFailure.Classify(new CryptographicException(unchecked((int)PinFailure.ScardWrongChv))) == PinFailureKind.Wrong,
    "pin: SCARD_W_WRONG_CHV classified as wrong PIN");
Check(PinFailure.Classify(new InvalidOperationException("x",
        new CryptographicException(unchecked((int)PinFailure.ScardChvBlocked)))) == PinFailureKind.Locked,
    "pin: SCARD_W_CHV_BLOCKED found in inner exception");
Check(PinFailure.Classify(new CryptographicException(unchecked((int)0x80090016))) == null,
    "pin: other CSP errors (e.g. keyset missing) are not PIN failures");

var pinDir = Path.Combine(Path.GetTempPath(), "rksp-pin-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(pinDir);
try
{
    using var pinRsa = RSA.Create(2048);
    using var pinCert = new CertificateRequest("CN=RemoteKSP PIN Probe", pinRsa, HashAlgorithmName.SHA256,
        RSASignaturePadding.Pkcs1).CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(1));
    var pinThumb = pinCert.Thumbprint.ToLowerInvariant();
    var certsJson = Path.Combine(pinDir, "certificates.json");
    File.WriteAllText(certsJson, JsonSerializer.Serialize(new
    {
        Certificates = new[] { new { Id = "cert-001", Name = "PIN 探针证书", Thumbprint = pinThumb, SourceType = "etoken",
            IsShared = true, AddedAt = DateTime.UtcNow, CspProvider = "Fake CSP", KeyContainer = "fake-container",
            PinEncrypted = DpapiHelper.ProtectString("wrong-pin") } }
    }));

    // 模拟 eToken：只接受 correct-pin；tokenLocked 时一律返回锁卡。所有实例共享访问计数
    var token = new FakeTokenState(pinRsa, pinCert.Export(X509ContentType.Cert));
    IKeySource FakeFactory(CertificateEntry e)
    {
        var source = new FakeETokenSource(token, e.Thumbprint);
        if (!string.IsNullOrEmpty(e.PinEncrypted))
            source.SetPin(DpapiHelper.UnprotectString(e.PinEncrypted));
        return source;
    }
    var pinRegistry = new CertificateRegistry(pinDir);
    var pinMgr = new KeySourceManager(pinRegistry, FakeFactory);
    var pinLog = new ActivityLogService();
    var notified = new System.Collections.Concurrent.ConcurrentQueue<(string Title, string Body)>();
    var alert = new PinAlertService(pinMgr, pinLog,
        Microsoft.Extensions.Logging.Abstractions.NullLogger<PinAlertService>.Instance,
        (title, body) => { notified.Enqueue((title, body)); return 1; });
    await alert.StartAsync(CancellationToken.None);
    Check(pinMgr.GetCertificateStatuses().Single() is { IsReady: true, DisabledReason: null },
        "pin: certificate with an (unverified) stored PIN starts ready");

    var hash = SHA256.HashData("pin"u8);
    var rejections = new System.Collections.Concurrent.ConcurrentBag<PinRejectedException>();
    Parallel.For(0, 8, _ =>
    {
        try { pinMgr.SignHash(pinThumb, hash, "SHA256"); }
        catch (PinRejectedException ex) { rejections.Add(ex); }
    });
    Check(rejections.Count == 8 && token.Calls == 1,
        "pin: 8 concurrent sign requests with a wrong PIN touch the eToken exactly once");
    Check(rejections.Count(r => r.JustDisabled) == 1 && rejections.All(r => r.Failure.Kind == PinFailureKind.Wrong),
        "pin: only the first rejection reports the transition to disabled");
    var disabledStatus = pinMgr.GetCertificateStatuses().Single();
    Check(disabledStatus is { IsReady: false, NeedsPin: false } && disabledStatus.DisabledReason == "The PIN saved on the server is wrong"
          && disabledStatus.Detail.StartsWith("Disabled"),
        "pin: status reports disabled with reason, not ready");
    Check(pinMgr.GetStatus().Detail.Contains("Disabled"), "pin: overview status shows the disabled detail");
    Check(Assert(() => pinMgr.SignHash(pinThumb, hash, "SHA256")) is { JustDisabled: false }, "pin: later sign requests fail fast");
    Check(token.Calls == 1, "pin: fail-fast request does not touch the eToken");

    Check(pinLog.GetRecentLogs().Count(l => l.Level == "ERROR" && l.Message.Contains("disabled")) == 1,
        "pin: activity log has exactly one disable Error");
    for (var i = 0; i < 50 && notified.IsEmpty; i++) await Task.Delay(20);
    Check(notified.Count == 1 && notified.Single().Body.Contains("PIN 探针证书") && notified.Single().Body.Contains("PIN saved by RemoteKSP is wrong"),
        "pin: exactly one Windows message sent with certificate name and reason");
    Check(notified.Single().Title.StartsWith("RemoteKSP") && notified.Single().Body.Contains("Windows service name: RemoteKSP") &&
          notified.Single().Body.Contains("\"Verify PIN\""),
        "pin: Windows message names RemoteKSP as the source and explains where to fix it");

    var persisted = JsonSerializer.Deserialize<JsonElement>(File.ReadAllText(certsJson)).GetProperty("Certificates")[0];
    Check(persisted.GetProperty("PinEncrypted").ValueKind == JsonValueKind.Null &&
          persisted.GetProperty("PinFailure").GetString() == "Wrong",
        "pin: wrong PIN deleted from certificates.json and reason persisted");

    // 服务重启：从磁盘加载，仍是停用状态，不会再拿 PIN 去试 token
    var restarted = new KeySourceManager(new CertificateRegistry(pinDir), FakeFactory);
    Check(restarted.GetCertificateStatuses().Single().DisabledReason != null, "pin: disabled state survives service restart");
    Check(Assert(() => restarted.SignHash(pinThumb, hash, "SHA256")) is { JustDisabled: false }, "pin: restarted service rejects signing");
    Check(token.Calls == 1 && restarted.RetryFailed(3).Outcome == RetryOutcome.NothingToRetry,
        "pin: restart and auto-retry never touch the eToken");

    // 重新设置正确 PIN：解除停用并恢复签名
    var verifier = (ETokenKeySource)FakeFactory(pinRegistry.GetById("cert-001")!);
    Check(verifier.VerifyPin("still-wrong") is { Success: false, Failure: PinFailureKind.Wrong },
        "pin: VerifyPin reports wrong PIN kind");
    Check(verifier.VerifyPin("correct-pin").Success, "pin: VerifyPin accepts the correct PIN");
    pinRegistry.SetPin("cert-001", "correct-pin");
    pinMgr.Reload();
    Check(pinMgr.GetCertificateStatuses().Single() is { IsReady: true, DisabledReason: null } &&
          pinMgr.SignHash(pinThumb, hash, "SHA256").Length == 256,
        "pin: setting a verified PIN re-enables the certificate");

    // 锁卡：停用原因不同
    token.Locked = true;
    var locked = Assert(() => pinMgr.SignHash(pinThumb, hash, "SHA256"));
    Check(locked is { JustDisabled: true, Failure.Kind: PinFailureKind.Locked } &&
          pinMgr.GetCertificateStatuses().Single().DisabledReason!.Contains("locked"),
        "pin: locked eToken disables certificate with locked reason");
    token.Locked = false;

    // 签名接口和 /certs：明确的错误码和停用原因
    var endpointClients = new ClientStore(pinDir);
    endpointClients.Add(new PairedClient { Id = "pin-client", MachineName = "pin-host", IpAddress = "127.0.0.1",
        SecretEncrypted = DpapiHelper.ProtectString("pin-secret"), PairedAt = DateTime.UtcNow, LastSeen = DateTime.UtcNow });
    var pinBuilder = WebApplication.CreateBuilder();
    pinBuilder.Logging.ClearProviders();
    pinBuilder.WebHost.ConfigureKestrel(k => k.Listen(IPAddress.Loopback, 0, o => o.UseHttps(certificate)));
    await using var pinApp = pinBuilder.Build();
    pinApp.UseRequestLanguage();
    pinApp.MapSigningEndpoints(endpointClients, new PairingManager(), pinMgr, pinLog);
    await pinApp.StartAsync();
    var pinAddress = pinApp.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.Single();
    using (var api = new ServerApi(pinAddress, "pin-client", "pin-secret", certificate.Thumbprint))
    {
        using var signResponse = await api.SendAsync(HttpMethod.Post, $"/sign/{pinThumb}", Convert.ToBase64String(hash));
        var signBody = await signResponse.Content.ReadFromJsonAsync<JsonElement>();
        Check(signResponse.StatusCode == HttpStatusCode.ServiceUnavailable &&
              signResponse.Headers.GetValues(Protocol.HeaderError).Single() == Protocol.ErrorPinLocked &&
              signBody.GetProperty("title").GetString() == "PIN Locked" &&
              signBody.GetProperty("detail").GetString()!.Contains("is disabled"),
            "pin endpoint: /sign returns 503 with pin-locked code and readable detail");
        Check(token.Calls == 5, "pin endpoint: rejected HTTP request does not touch the eToken");

        var remoteCerts = await api.GetCertificatesAsync();
        Check(remoteCerts.Single().DisabledReason?.Contains("locked") == true, "pin endpoint: /certs exposes the disabled reason");
    }
    // 服务端按客户端界面语言（Accept-Language）返回说明，与服务进程自己的语言无关
    UiLanguage.Apply(UiLanguage.ChineseSimplified);
    using (var zhApi = new ServerApi(pinAddress, "pin-client", "pin-secret", certificate.Thumbprint))
    {
        UiLanguage.Apply(UiLanguage.English);
        var zhCerts = await zhApi.GetCertificatesAsync();
        Check(zhCerts.Single().DisabledReason?.Contains("已锁定") == true,
            "localization: the server answers in the language the client asks for");
    }
    var server = new ServerEntry("server-001", pinAddress, "PIN 服务端", "pin-client", "pin-secret", certificate.Thumbprint);
    var offline = new ServerEntry("server-002", "https://127.0.0.1:1", "离线服务端", "x", "y", certificate.Thumbprint);
    var disabledMap = await ServerManager.GetDisabledCertificatesAsync([server, offline], TimeSpan.FromSeconds(5));
    Check(disabledMap.Count == 1 && disabledMap[("server-001", pinThumb)].Contains("locked"),
        "pin client: disabled certificates collected per server, offline server skipped");
    var clientItem = new RemoteKSP.Client.ViewModels.ClientCertItem(
        new CertificateBinding(pinThumb, "server-001", "PIN 探针证书", DateTime.UtcNow, false), "PIN 服务端",
        disabledMap[("server-001", pinThumb)]);
    Check(clientItem.StatusText == "Disabled by server" && clientItem.StatusKind == "Critical",
        "pin client: certificate list shows server-disabled state");
    Check(pinLog.GetRecentLogs().Any(l => l.Level == "ERROR" && l.Message.Contains("pin-host") && l.Message.Contains("disabled")),
        "pin endpoint: rejected client request logged as Error with client name");
    await pinApp.StopAsync();
    await alert.StopAsync(CancellationToken.None);
}
finally
{
    Directory.Delete(pinDir, true);
}

// GUI 验证 PIN 的连续失败上限：3 次失败后锁 1 分钟，到期恢复，成功清零
var limiterClock = new TestClock();
var limiter = new PinAttemptLimiter(limiterClock);
var limiterKey = PinAttemptLimiter.KeyFor("eToken Base Cryptographic Provider", "Container-A");
Check(limiter.RecordFailure(limiterKey) == 2 && limiter.RecordFailure(limiterKey) == 1 && limiter.GetLockout(limiterKey) == null,
    "pin limiter: first two failures report remaining attempts, no lockout");
Check(limiter.RecordFailure(limiterKey) == 0 && limiter.GetLockout(limiterKey) == TimeSpan.FromMinutes(1),
    "pin limiter: third failure locks for 1 minute");
Check(limiter.GetLockout(PinAttemptLimiter.KeyFor("EToken base cryptographic provider", "container-a")) != null &&
      limiter.GetLockout(PinAttemptLimiter.KeyFor("eToken Base Cryptographic Provider", "Container-B")) == null,
    "pin limiter: lockout is per container, case-insensitive");
limiterClock.Advance(TimeSpan.FromSeconds(59));
Check(limiter.GetLockout(limiterKey) == TimeSpan.FromSeconds(1), "pin limiter: still locked at 59 seconds");
limiterClock.Advance(TimeSpan.FromSeconds(1));
Check(limiter.GetLockout(limiterKey) == null && limiter.RecordFailure(limiterKey) == 2,
    "pin limiter: lockout expires after 1 minute with a fresh budget");
limiter.RecordSuccess(limiterKey);
Check(limiter.RecordFailure(limiterKey) == 2, "pin limiter: success resets the failure count");

// ServiceManager 改为隐藏窗口运行 sc.exe：只读查询不需要管理员，能验证输出捕获和中文解码
var runHidden = typeof(ServiceManager).GetMethod("RunHiddenAsync",
    System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)!;
var (missingCode, missingOutput) = await (Task<(int, string)>)runHidden.Invoke(null,
    ["sc.exe", "query RemoteKSPDefinitelyNotInstalled"])!;
Console.WriteLine($"  sc query (missing service): exit {missingCode}: {missingOutput.Replace("\n", " ")}");
Check(missingCode == 1060 && missingOutput.Contains("1060") && !missingOutput.Contains('�'),
    "service manager: hidden sc.exe output captured and decoded without mojibake");

// 生产 DI：用 --service 模式的真实 HostBuilder 构建并解析所有托管服务（只构造，不启动）
var createHost = typeof(RemoteKSP.Server.Program).GetMethod("CreateHostBuilder",
    System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)!;
using (var serviceHost = ((IHostBuilder)createHost.Invoke(null, [Array.Empty<string>()])!).Build())
{
    var hosted = serviceHost.Services.GetServices<IHostedService>().Select(s => s.GetType().Name).ToList();
    Check(hosted.Contains(nameof(KeySourceRetryService)) && hosted.Contains("SigningHostedService"),
        "production DI resolves KeySourceRetryService alongside the signing service");
    Check(hosted.Contains(nameof(PipeServer)) &&
          hosted.IndexOf(nameof(PinAlertService)) is >= 0 and var alertIndex && alertIndex < hosted.IndexOf("SigningHostedService"),
        "production DI resolves PipeServer and starts PinAlertService before the signing service");
}

Console.WriteLine("\n--- Feature Tests: SignTool Runner ---");
var echoRun = await SignToolRunner.RunAsync("echo runner-ok & exit /b 7");
Check(echoRun.ExitCode == 7 && echoRun.Output.Contains("runner-ok"), "runner: captures output and exit code");
// 超时：ping 要跑约 30 秒，1 秒后应结束整个进程树
var pingsBefore = Process.GetProcessesByName("PING").Select(p => p.Id).ToHashSet();
var slowRun = await SignToolRunner.RunAsync("ping -n 30 127.0.0.1", timeout: TimeSpan.FromSeconds(1));
await Task.Delay(300);
var leftover = Process.GetProcessesByName("PING").Count(p => !pingsBefore.Contains(p.Id));
Console.WriteLine($"  runner: timed out after {slowRun.Elapsed.TotalSeconds:F1}s, leftover ping processes: {leftover}");
Check(slowRun.TimedOut && slowRun.ExitCode == null && slowRun.Elapsed < TimeSpan.FromSeconds(10) && leftover == 0,
    "runner: timeout kills the whole process tree");

Console.WriteLine("\n--- Key and data safety ---");

// ETokenKeySource 签名后不能删除密钥容器（PersistKeyInCsp = false 会在 Dispose 时 CRYPT_DELETEKEYSET）。
// 用软件 CSP 建一个和 ETokenKeySource 拼出的名字（[{{PIN}}]=容器）完全相同的临时容器
{
    const string softwareCsp = "Microsoft Enhanced Cryptographic Provider v1.0";
    const string probePin = "probe-pin";
    var probeContainer = $"remoteksp-regression-{Guid.NewGuid():N}";
    CspParameters ProbeParams(CspProviderFlags flags) =>
        new(1, softwareCsp, $"[{{{{{probePin}}}}}]={probeContainer}") { Flags = flags | CspProviderFlags.UseMachineKeyStore };
    bool ContainerExists()
    {
        try { using var _ = new RSACryptoServiceProvider(ProbeParams(CspProviderFlags.UseExistingKey)); return true; }
        catch (CryptographicException) { return false; }
    }

    using (var created = new RSACryptoServiceProvider(2048, ProbeParams(CspProviderFlags.NoFlags))) { created.PersistKeyInCsp = true; }
    try
    {
        var probe = new ETokenKeySource(softwareCsp, probeContainer, "", Microsoft.Extensions.Logging.Abstractions.NullLogger<ETokenKeySource>.Instance);
        probe.SetPin(probePin);
        // 软件 PROV_RSA_FULL 不支持 SHA256（eToken CSP 支持），这里用 SHA1 走同一条 CreateRsa/Dispose 路径
        Check(probe.SignHash(new byte[20], "SHA1").Length == 256, "etoken source: signature with PIN-embedded container name succeeds");
        Check(ContainerExists(), "etoken source: key container still exists after signing (never CRYPT_DELETEKEYSET)");
    }
    finally
    {
        if (ContainerExists())
            using (var cleanup = new RSACryptoServiceProvider(ProbeParams(CspProviderFlags.UseExistingKey))) { cleanup.PersistKeyInCsp = false; }
    }
}

// 数据目录 ACL：普通用户读不到 DPAPI LocalMachine 加密的 PIN、客户端密钥和 TLS 私钥
{
    var aclDir = Path.Combine(Path.GetTempPath(), $"remoteksp-acl-{Guid.NewGuid():N}");
    var nested = Directory.CreateDirectory(Path.Combine(aclDir, "pfx"));
    var topFile = Path.Combine(aclDir, "certificates.json");
    var nestedFile = Path.Combine(nested.FullName, "cert.pfx");
    File.WriteAllText(topFile, "{}");
    File.WriteAllText(nestedFile, "x");
    var users = new SecurityIdentifier(WellKnownSidType.BuiltinUsersSid, null);
    var explicitUsers = new FileInfo(nestedFile).GetAccessControl();
    explicitUsers.AddAccessRule(new FileSystemAccessRule(users, FileSystemRights.Read, AccessControlType.Allow));
    new FileInfo(nestedFile).SetAccessControl(explicitUsers);

    var allowed = new HashSet<SecurityIdentifier>
    {
        new(WellKnownSidType.LocalSystemSid, null), new(WellKnownSidType.BuiltinAdministratorsSid, null)
    };
    bool OnlyAllowed(FileSystemSecurity s) => s.GetAccessRules(true, true, typeof(SecurityIdentifier))
        .Cast<FileSystemAccessRule>().All(r => allowed.Contains((SecurityIdentifier)r.IdentityReference));
    try
    {
        SecureStorage.EnsureRestrictedDirectory(aclDir);
        var dirAcl = new DirectoryInfo(aclDir).GetAccessControl();
        Check(dirAcl.AreAccessRulesProtected && OnlyAllowed(dirAcl),
            "data dir: inheritance from ProgramData removed, only SYSTEM and Administrators allowed");
        Check(OnlyAllowed(new FileInfo(topFile).GetAccessControl()), "data dir: existing file no longer readable by Users");
        Check(OnlyAllowed(new FileInfo(nestedFile).GetAccessControl()),
            "data dir: explicit Users ACE on nested file removed");

        var freshDir = Path.Combine(aclDir + "-new");
        SecureStorage.EnsureRestrictedDirectory(freshDir);
        var freshAcl = new DirectoryInfo(freshDir).GetAccessControl();
        Check(freshAcl.AreAccessRulesProtected && OnlyAllowed(freshAcl), "data dir: newly created directory is restricted from the start");
        RestoreAndDelete(freshDir);
    }
    finally
    {
        RestoreAndDelete(aclDir);
    }

    // 收紧后当前（未提权）进程也进不去：作为所有者改回 ACL 再删除
    static void RestoreAndDelete(string path)
    {
        if (!Directory.Exists(path)) return;
        var me = WindowsIdentity.GetCurrent().User!;
        var open = new DirectorySecurity();
        open.AddAccessRule(new FileSystemAccessRule(me, FileSystemRights.FullControl,
            InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit, PropagationFlags.None, AccessControlType.Allow));
        new DirectoryInfo(path).SetAccessControl(open);
        Directory.Delete(path, true);
    }
}

// 数据目录被预先放了联接（普通用户能在 ProgramData 下建目录）：拒绝使用，不写入、不改动链接和目标
{
    var jxRoot = Directory.CreateTempSubdirectory("remoteksp-jx-").FullName;
    var target = Directory.CreateDirectory(Path.Combine(jxRoot, "target")).FullName;
    var linkedData = Path.Combine(jxRoot, "RemoteKSP");
    var realData = Path.Combine(jxRoot, "data");
    var nestedLink = Path.Combine(realData, "nested", "pfx");
    void Junction(string link, string to)
    {
        using var mk = Process.Start(new ProcessStartInfo("cmd.exe", $"/d /c mklink /J \"{link}\" \"{to}\"")
            { UseShellExecute = false, RedirectStandardOutput = true, CreateNoWindow = true })!;
        mk.WaitForExit();
        if ((File.GetAttributes(link) & FileAttributes.ReparsePoint) == 0)
            throw new Exception("FAIL: test junction not created");
    }
    bool Rejected(string path)
    {
        try { SecureStorage.EnsureRestrictedDirectory(path); return false; }
        catch (UnauthorizedAccessException ex) { return ex.Message.Contains("junction"); }
    }
    string AclOf(string path) =>
        new DirectoryInfo(path).GetAccessControl().GetSecurityDescriptorSddlForm(AccessControlSections.Access);
    try
    {
        Junction(linkedData, target);
        var targetBefore = AclOf(target);
        var linkBefore = AclOf(linkedData);
        Check(Rejected(linkedData), "data dir: junction planted as the data directory is rejected");
        Check(AclOf(target) == targetBefore && AclOf(linkedData) == linkBefore && Directory.GetFiles(target).Length == 0,
            "data dir: rejected junction and its target left untouched");

        Directory.CreateDirectory(Path.Combine(realData, "nested"));
        Junction(nestedLink, target);
        var realBefore = AclOf(realData);
        Check(Rejected(realData) && AclOf(realData) == realBefore && AclOf(target) == targetBefore,
            "data dir: junction planted inside the data directory is rejected before anything is changed");
    }
    finally
    {
        // 清理失败（例如防护失效、目标被收紧）不能盖住上面的 FAIL
        try
        {
            foreach (var link in new[] { linkedData, nestedLink })
                if (Directory.Exists(link)) Directory.Delete(link);
            Directory.Delete(jxRoot, true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Console.WriteLine($"  junction test cleanup failed, remove manually: {jxRoot} ({ex.Message})");
        }
    }
}

// TLS 证书：客户端按指纹固定它，server.pfx 读不出来时报错，不重新生成（换证书后所有客户端都要重新配对）
{
    var tlsDir = Directory.CreateTempSubdirectory("remoteksp-tls-").FullName;
    var tlsPath = Path.Combine(tlsDir, "server.pfx");
    // 未提权时机器密钥可能根本没落盘（密钥集不存在），清理时忽略
    static void DeletePersistedKey(X509Certificate2 c)
    {
        try
        {
            using var k = c.GetRSAPrivateKey();
            if (k is RSACng cng) cng.Key.Delete();
        }
        catch (CryptographicException) { }
    }
    try
    {
        // 新生成证书要写机器密钥库（服务以 SYSTEM 运行），未提权的测试进程做不到；这里直接准备一份 server.pfx
        using (var tlsKey = RSA.Create(2048))
        using (var original = new CertificateRequest("CN=RemoteKSP Server", tlsKey, HashAlgorithmName.SHA256,
                   RSASignaturePadding.Pkcs1).CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(10)))
        {
            File.WriteAllBytes(tlsPath, DpapiHelper.Protect(original.Export(X509ContentType.Pfx)));
            var originalBytes = File.ReadAllBytes(tlsPath);
            using var reloaded = TlsCertificateManager.LoadOrCreate(tlsPath);
            Check(reloaded.Thumbprint == original.Thumbprint && File.ReadAllBytes(tlsPath).SequenceEqual(originalBytes),
                "tls: existing server.pfx reused even when it expires within 30 days");
            DeletePersistedKey(reloaded);
        }

        var garbage = "not a dpapi-protected pfx"u8.ToArray();
        File.WriteAllBytes(tlsPath, garbage);
        MustThrow(() => TlsCertificateManager.LoadOrCreate(tlsPath),
            "tls: unreadable server.pfx raises an error instead of generating a new certificate");
        Check(File.ReadAllBytes(tlsPath).SequenceEqual(garbage), "tls: unreadable server.pfx left untouched");
    }
    finally
    {
        Directory.Delete(tlsDir, true);
    }
}

// 设置里"重新生成 TLS 证书"：原文件备份、新证书可被 Kestrel 使用，并且不重启监听就换到新证书
{
    var regenDir = Directory.CreateTempSubdirectory("remoteksp-regen-").FullName;
    var regenPath = Path.Combine(regenDir, "server.pfx");
    var generatedCerts = new List<X509Certificate2>();
    try
    {
        // 1) 原文件读不出来（最需要重新生成的情况）：照样备份原文件，生成新证书
        var damaged = "damaged server.pfx"u8.ToArray();
        File.WriteAllBytes(regenPath, damaged);
        var first = TlsCertificateManager.Regenerate(regenPath, out var firstCert);
        generatedCerts.Add(firstCert);
        Check(first.OldThumbprint == null && first.BackupFile != null &&
              File.ReadAllBytes(Path.Combine(regenDir, first.BackupFile)).SequenceEqual(damaged),
            "tls regenerate: unreadable server.pfx backed up byte for byte");
        using (var loaded = TlsCertificateManager.LoadOrCreate(regenPath))
            Check(loaded.Thumbprint.ToLowerInvariant() == first.NewThumbprint && loaded.HasPrivateKey,
                "tls regenerate: new server.pfx readable with private key");

        // 2) 用 Kestrel 的证书选择器（和 WebHostBuilder 相同）提供证书，重新生成后新连接拿到新证书。
        // 服务以 SYSTEM 运行，从机器密钥库加载；未提权的测试进程写不了机器密钥库，这里从生成的 server.pfx 按当前用户加载
        X509Certificate2 ServeFromFile()
        {
            var c = X509CertificateLoader.LoadPkcs12(DpapiHelper.Unprotect(File.ReadAllBytes(regenPath)), null,
                X509KeyStorageFlags.Exportable);
            generatedCerts.Add(c);
            return c;
        }
        var serving = ServeFromFile();
        var tlsBuilder = WebApplication.CreateBuilder();
        tlsBuilder.Logging.ClearProviders();
        tlsBuilder.WebHost.ConfigureKestrel(k => k.Listen(IPAddress.Loopback, 0,
            o => o.UseHttps(h => h.ServerCertificateSelector = (_, _) => serving)));
        await using var tlsApp = tlsBuilder.Build();
        tlsApp.MapGet("/info", () => "ok");
        await tlsApp.StartAsync();
        var tlsAddress = tlsApp.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.Single();
        async Task<string?> PresentedThumbprint()
        {
            string? seen = null;
            using var handler = new HttpClientHandler
            {
                ServerCertificateCustomValidationCallback = (_, c, _, _) => { seen = c?.Thumbprint.ToLowerInvariant(); return true; }
            };
            using var http = new HttpClient(handler);
            await http.GetStringAsync(tlsAddress + "/info");
            return seen;
        }
        Check(await PresentedThumbprint() == first.NewThumbprint, "tls regenerate: Kestrel serves the generated certificate");

        var second = TlsCertificateManager.Regenerate(regenPath, out var secondCert);
        generatedCerts.Add(secondCert);
        serving = ServeFromFile();
        Check(second.OldThumbprint == first.NewThumbprint && second.NewThumbprint != first.NewThumbprint &&
              second.BackupFile != first.BackupFile,
            "tls regenerate: second run reports the previous thumbprint and keeps a separate backup");
        Check(await PresentedThumbprint() == second.NewThumbprint,
            "tls regenerate: new connections get the new certificate without restarting the listener");
        await tlsApp.StopAsync();
    }
    finally
    {
        foreach (var c in generatedCerts)
        {
            try { using var k = c.GetRSAPrivateKey(); if (k is RSACng cng) cng.Key.Delete(); }
            catch (CryptographicException) { }
            c.Dispose();
        }
        Directory.Delete(regenDir, true);
    }
}

// Reload 发生在签名途中（GUI 改了任意证书）：新旧密钥源共用"刚被拒绝的 PIN"，同一个错误 PIN 只访问 token 一次
{
    var raceDir = Directory.CreateTempSubdirectory("remoteksp-race-").FullName;
    try
    {
        using var raceRsa = RSA.Create(2048);
        using var raceCert = new CertificateRequest("CN=RemoteKSP Reload Probe", raceRsa, HashAlgorithmName.SHA256,
            RSASignaturePadding.Pkcs1).CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(1));
        var raceThumb = raceCert.Thumbprint.ToLowerInvariant();
        var raceContainer = $"reload-{Guid.NewGuid():N}";
        File.WriteAllText(Path.Combine(raceDir, "certificates.json"), JsonSerializer.Serialize(new
        {
            Certificates = new[] { new { Id = "cert-001", Name = "Reload 探针证书", Thumbprint = raceThumb, SourceType = "etoken",
                IsShared = true, AddedAt = DateTime.UtcNow, CspProvider = "Fake CSP", KeyContainer = raceContainer,
                PinEncrypted = DpapiHelper.ProtectString("wrong-pin") } }
        }));
        var raceToken = new FakeTokenState(raceRsa, raceCert.Export(X509ContentType.Cert)) { Gate = new ManualResetEventSlim(false) };
        IKeySource RaceFactory(CertificateEntry e)
        {
            var source = new FakeETokenSource(raceToken, e.Thumbprint, raceContainer);
            if (!string.IsNullOrEmpty(e.PinEncrypted))
                source.SetPin(DpapiHelper.UnprotectString(e.PinEncrypted));
            return source;
        }
        var raceMgr = new KeySourceManager(new CertificateRegistry(raceDir), RaceFactory);
        var raceHash = SHA256.HashData("reload"u8);

        var inFlight = Task.Run(() => Assert(() => raceMgr.SignHash(raceThumb, raceHash, "SHA256")));
        Check(raceToken.Entered.Wait(TimeSpan.FromSeconds(5)), "pin reload: first request reached the eToken");
        raceMgr.Reload(); // 新对象仍带着同一个（还没被标记的）错误 PIN
        raceToken.Gate!.Set();
        Check((await inFlight).JustDisabled, "pin reload: in-flight request rejected by the eToken");

        var afterReload = Assert(() => raceMgr.SignHash(raceThumb, raceHash, "SHA256"));
        Check(raceToken.Calls == 1 && afterReload.JustDisabled,
            "pin reload: key source created during the request does not retry the rejected PIN on the eToken");
        Check(new CertificateRegistry(raceDir).GetById("cert-001")!.PinFailure == PinFailureKind.Wrong,
            "pin reload: disabled state persisted for the reloaded certificate");
    }
    finally
    {
        Directory.Delete(raceDir, true);
    }
}

// 服务程序固定装在 Program Files\RemoteKSP：注册路径加引号、部署可回滚、普通用户可写的目录拒绝、卸载不跟随链接删除
{
    // binPath：sc.exe 收到的参数和 SCM 存下的 ImagePath 都要把带空格的 exe 路径当成一个整体
    var spacedExe = @"C:\Program Files\RemoteKSP\RemoteKSP.Server.exe";
    var argv = SplitCommandLine($"sc.exe create RemoteKSP {ServiceProgram.BinPathArgument(spacedExe)} start= auto");
    // argv: sc.exe create RemoteKSP binPath= <ImagePath> start= auto
    Check(argv.Length == 7 && argv[3] == "binPath=" && argv[4] == $"\"{spacedExe}\" --service" && argv[5] == "start=",
        "service program: binPath keeps a path with spaces quoted as one ImagePath");
    Check(ServiceProgram.ExeFromImagePath(argv[4]) == spacedExe &&
          ServiceProgram.ExeFromImagePath(@"D:\Builds\RemoteKSP\Publish\Server\RemoteKSP.Server.exe --service")
              == @"D:\Builds\RemoteKSP\Publish\Server\RemoteKSP.Server.exe" &&
          ServiceProgram.ExeFromImagePath(null) == null,
        "service program: exe path read from quoted and legacy unquoted ImagePath");
    Check(string.Equals(ServiceProgram.InstalledExe,
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "RemoteKSP", "RemoteKSP.Server.exe"),
            StringComparison.OrdinalIgnoreCase),
        "service program: installed under %ProgramFiles%\\RemoteKSP");
    Check(!ServiceProgram.WritableByNonAdmins(new DirectoryInfo(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles))),
        "service program: Program Files itself is not reported as user-writable");

    var spDir = Directory.CreateTempSubdirectory("remoteksp-svcprog-").FullName;
    try
    {
        var source = Path.Combine(spDir, "source", ServiceProgram.ExeName);
        Directory.CreateDirectory(Path.GetDirectoryName(source)!);
        var target = Path.Combine(spDir, "install", ServiceProgram.ExeName);
        File.WriteAllBytes(source, [1, 2, 3, 4]);

        ServiceProgram.DeployExecutable(source, target);
        Check(File.ReadAllBytes(target).SequenceEqual(new byte[] { 1, 2, 3, 4 }) && ServiceProgram.SameBuild(source, target),
            "deploy: fresh install copies the service program");

        File.WriteAllBytes(source, [9, 9, 9, 9]);
        Check(!ServiceProgram.SameBuild(source, target), "deploy: different build of the same size detected");
        ServiceProgram.DeployExecutable(source, target);
        Check(File.ReadAllBytes(target).SequenceEqual(new byte[] { 9, 9, 9, 9 }) &&
              Directory.GetFiles(Path.GetDirectoryName(target)!).Length == 1,
            "deploy: update replaces the program and leaves no .new/.old files");

        ServiceProgram.DeployExecutable(target, target);
        Check(File.ReadAllBytes(target).SequenceEqual(new byte[] { 9, 9, 9, 9 }), "deploy: running from the install location is a no-op");

        // 目标被占用、不能改名：失败，原文件不变，不留临时文件
        File.WriteAllBytes(source, [5, 5, 5, 5, 5]);
        using (new FileStream(target, FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            try { ServiceProgram.DeployExecutable(source, target); Check(false, "deploy: locked target must fail"); }
            catch (IOException) { }
        }
        Check(File.ReadAllBytes(target).SequenceEqual(new byte[] { 9, 9, 9, 9 }) &&
              Directory.GetFiles(Path.GetDirectoryName(target)!).Length == 1,
            "deploy: failure keeps the old program and removes the temp copy");

        // 普通用户可写的目录：拒绝部署（服务以 SYSTEM 运行）
        var unsafeDir = Directory.CreateDirectory(Path.Combine(spDir, "unsafe"));
        var unsafeAcl = unsafeDir.GetAccessControl();
        unsafeAcl.AddAccessRule(new FileSystemAccessRule(new SecurityIdentifier(WellKnownSidType.AuthenticatedUserSid, null),
            FileSystemRights.Modify, InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit,
            PropagationFlags.None, AccessControlType.Allow));
        unsafeDir.SetAccessControl(unsafeAcl);
        Check(ServiceProgram.WritableByNonAdmins(unsafeDir), "service program: Authenticated Users Modify reported as user-writable");
        var unsafeTarget = Path.Combine(unsafeDir.FullName, ServiceProgram.ExeName);
        try { ServiceProgram.DeployExecutable(source, unsafeTarget); Check(false, "deploy: user-writable dir must be refused"); }
        catch (UnauthorizedAccessException) { }
        Check(!File.Exists(unsafeTarget), "deploy: refuses a user-writable install directory");

        // 卸载：只删服务程序和升级留下的旧文件；正在运行的那个保留
        var installDir = Path.GetDirectoryName(target)!;
        File.WriteAllText(target + ".old-20260101", "old");
        File.WriteAllText(Path.Combine(installDir, "notes.txt"), "keep");
        Check(ServiceProgram.RemoveInstalledProgram(installDir, runningExe: null) == null &&
              !File.Exists(target) && !File.Exists(target + ".old-20260101") && File.Exists(Path.Combine(installDir, "notes.txt")),
            "uninstall: removes the service program and old copies, keeps unrelated files");
        File.Delete(Path.Combine(installDir, "notes.txt"));
        File.WriteAllBytes(target, [1]);
        Check(ServiceProgram.RemoveInstalledProgram(installDir, runningExe: target) == target && File.Exists(target),
            "uninstall: the exe the GUI is running from is kept and reported");
        File.Delete(target);
        ServiceProgram.RemoveInstalledProgram(installDir, null);
        Check(!Directory.Exists(installDir), "uninstall: empty install directory removed");

        // 删除数据目录：正常删除；有联接时拒绝，不跟着链接删到别处
        var data = Path.Combine(spDir, "data");
        Directory.CreateDirectory(Path.Combine(data, "pfx"));
        File.WriteAllText(Path.Combine(data, "certificates.json"), "{}");
        File.WriteAllText(Path.Combine(data, "pfx", "a.pfx"), "x");
        File.SetAttributes(Path.Combine(data, "certificates.json"), FileAttributes.ReadOnly);
        ServiceProgram.RemoveDataDirectory(data);
        Check(!Directory.Exists(data), "uninstall: data directory removed, including read-only files");

        var victim = Directory.CreateDirectory(Path.Combine(spDir, "victim")).FullName;
        File.WriteAllText(Path.Combine(victim, "important.txt"), "x");
        Directory.CreateDirectory(data);
        File.WriteAllText(Path.Combine(data, "config.json"), "{}");
        var link = Path.Combine(data, "pfx");
        using (var mk = Process.Start(new ProcessStartInfo("cmd.exe", $"/d /c mklink /J \"{link}\" \"{victim}\"")
                   { UseShellExecute = false, RedirectStandardOutput = true, CreateNoWindow = true })!)
            mk.WaitForExit();
        try { ServiceProgram.RemoveDataDirectory(data); Check(false, "uninstall: junction must be refused"); }
        catch (UnauthorizedAccessException) { }
        Check(File.Exists(Path.Combine(victim, "important.txt")) && File.Exists(Path.Combine(data, "config.json")),
            "uninstall: data directory with a junction is not deleted and the link target is untouched");
        Directory.Delete(link);

        using var listener = new System.Net.Sockets.TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        Check(ServiceManager.IsPortInUse(((IPEndPoint)listener.LocalEndpoint).Port), "port: a port already listened on is reported in use");
        listener.Stop();
    }
    finally
    {
        Directory.Delete(spDir, true);
    }
}

// eToken 没插 / 卡忙 / 错误码不认识：重试不能多耗 PIN 重试次数，插上后自己恢复
{
    var absentDir = Directory.CreateTempSubdirectory("remoteksp-absent-").FullName;
    try
    {
        using var absentRsa = RSA.Create(2048);
        using var absentCert = new CertificateRequest("CN=RemoteKSP Absent Probe", absentRsa, HashAlgorithmName.SHA256,
            RSASignaturePadding.Pkcs1).CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(1));
        var absentThumb = absentCert.Thumbprint.ToLowerInvariant();
        var absentContainer = $"absent-{Guid.NewGuid():N}";
        File.WriteAllText(Path.Combine(absentDir, "certificates.json"), JsonSerializer.Serialize(new
        {
            Certificates = new[] { new { Id = "cert-001", Name = "未插入探针", Thumbprint = absentThumb, SourceType = "etoken",
                IsShared = true, AddedAt = DateTime.UtcNow, CspProvider = "Fake CSP", KeyContainer = absentContainer,
                PinEncrypted = DpapiHelper.ProtectString("correct-pin") } }
        }));
        var absentToken = new FakeTokenState(absentRsa, absentCert.Export(X509ContentType.Cert)) { Present = false };
        IKeySource AbsentFactory(CertificateEntry e)
        {
            var s = new FakeETokenSource(absentToken, e.Thumbprint, absentContainer);
            if (!string.IsNullOrEmpty(e.PinEncrypted)) s.SetPin(DpapiHelper.UnprotectString(e.PinEncrypted));
            return s;
        }
        var absentMgr = new KeySourceManager(new CertificateRegistry(absentDir), AbsentFactory);
        var absentHash = SHA256.HashData("absent"u8);
        bool SignFailsRetryable()
        {
            try { absentMgr.SignHash(absentThumb, absentHash, "SHA256"); return false; }
            catch (PinRejectedException) { return false; }
            catch (InvalidOperationException) { return true; }
        }

        // 启动时没插：照样加载（不再按容器枚举拦截），状态显示"未检测到"
        var startStatus = absentMgr.GetCertificateStatuses().Single();
        Check(!startStatus.IsReady && startStatus.DisabledReason == null && startStatus.Detail.Contains("No eToken detected"),
            "etoken absent: loaded anyway, status says not detected (not disabled)");
        Check(Enumerable.Range(0, 5).All(_ => SignFailsRetryable()) && absentToken.PinChecks == 0,
            "etoken absent: 5 sign requests fail as retryable and never reach the PIN check");
        Check(absentMgr.GetCertificateStatuses().Single().DisabledReason == null &&
              new CertificateRegistry(absentDir).GetById("cert-001")!.PinEncrypted != null,
            "etoken absent: certificate not disabled and the saved PIN kept");

        // 插上：不用重启、不用点重新加载，下一个请求直接签
        absentToken.Present = true;
        Check(absentMgr.GetCertificateStatuses().Single().IsReady, "etoken inserted: status recovers without a reload");
        Check(absentMgr.SignHash(absentThumb, absentHash, "SHA256").Length == 256 && absentToken.PinChecks == 1,
            "etoken inserted: next request signs with one PIN check");

        // 卡忙（发生在校验 PIN 之前）：可以重试，不停用
        absentToken.Error = unchecked((int)PinFailure.ScardSharingViolation);
        var checksBefore = absentToken.PinChecks;
        Check(SignFailsRetryable() && SignFailsRetryable() && absentToken.PinChecks == checksBefore &&
              absentMgr.GetCertificateStatuses().Single().DisabledReason == null,
            "etoken busy (sharing violation): retryable, no PIN check, not disabled");

        // 插着但错误码不认识（可能是 CSP 用别的码表示 PIN 错误）：只试一次就停用，之后不再访问 token
        absentToken.Error = unchecked((int)0x80090020); // NTE_FAIL
        var callsBefore = absentToken.Calls;
        var unknown = Assert(() => absentMgr.SignHash(absentThumb, absentHash, "SHA256"));
        Check(unknown is { JustDisabled: true, Failure.Kind: PinFailureKind.Unrecognized },
            "etoken unrecognized error: certificate disabled after the first failure");
        for (var i = 0; i < 5; i++)
            Assert(() => absentMgr.SignHash(absentThumb, absentHash, "SHA256"));
        Check(absentToken.Calls == callsBefore + 1 && absentToken.PinChecks == checksBefore + 1,
            "etoken unrecognized error: 5 more requests never touch the eToken (one PIN check in total)");
        var persistedUnknown = new CertificateRegistry(absentDir).GetById("cert-001")!;
        Check(persistedUnknown.PinFailure == PinFailureKind.Unrecognized && persistedUnknown.PinEncrypted == null,
            "etoken unrecognized error: disabled state persisted and saved PIN deleted");
        var unknownReason = absentMgr.GetCertificateStatuses().Single().DisabledReason!;
        var (_, unknownMessage) = PinAlertService.BuildMessage(
            new PinRejectedEvent("cert-001", "未插入探针", absentThumb, unknown.Failure), @"C:\Program Files\RemoteKSP\RemoteKSP.Server.exe");
        Check(unknownReason.Contains("unrecognized") && unknownMessage.Contains("unrecognized error") && unknownMessage.Contains("Verify PIN"),
            "etoken unrecognized error: status and Windows message explain the reason and the fix");
    }
    finally
    {
        Directory.Delete(absentDir, true);
    }
}

// 证书已从存储删掉（或驱动没就绪）导致不可用：签名请求到来时按需重新加载，不用等自动重试
{
    var reloadDir = Directory.CreateTempSubdirectory("remoteksp-ondemand-").FullName;
    try
    {
        using var odRsa = RSA.Create(2048);
        using var odCert = new CertificateRequest("CN=RemoteKSP OnDemand Probe", odRsa, HashAlgorithmName.SHA256,
            RSASignaturePadding.Pkcs1).CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(1));
        var odThumb = odCert.Thumbprint.ToLowerInvariant();
        File.WriteAllText(Path.Combine(reloadDir, "certificates.json"), JsonSerializer.Serialize(new
        {
            Certificates = new[] { new { Id = "cert-001", Name = "按需加载探针", Thumbprint = odThumb, SourceType = "etoken",
                IsShared = true, AddedAt = DateTime.UtcNow, CspProvider = "Fake CSP", KeyContainer = $"od-{Guid.NewGuid():N}",
                PinEncrypted = DpapiHelper.ProtectString("correct-pin") } }
        }));
        var odToken = new FakeTokenState(odRsa, odCert.Export(X509ContentType.Cert));
        var available = false;
        var loads = 0;
        IKeySource OdFactory(CertificateEntry e)
        {
            Interlocked.Increment(ref loads);
            if (!available) throw new InvalidOperationException("证书不在 LocalMachine\\My");
            var s = new FakeETokenSource(odToken, e.Thumbprint);
            s.SetPin(DpapiHelper.UnprotectString(e.PinEncrypted!));
            return s;
        }
        var odMgr = new KeySourceManager(new CertificateRegistry(reloadDir), OdFactory, TimeSpan.FromMinutes(1));
        var odHash = SHA256.HashData("ondemand"u8);
        try { odMgr.SignHash(odThumb, odHash, "SHA256"); } catch (InvalidOperationException) { }
        var afterFirst = loads;
        try { odMgr.SignHash(odThumb, odHash, "SHA256"); } catch (InvalidOperationException) { }
        Check(afterFirst == 2 && loads == 2, "on-demand reload: unavailable certificate reloaded once, then rate limited");

        var fastMgr = new KeySourceManager(new CertificateRegistry(reloadDir), OdFactory, TimeSpan.Zero);
        available = true;
        Check(fastMgr.SignHash(odThumb, odHash, "SHA256").Length == 256 && fastMgr.GetCertificateStatuses().Single().IsReady,
            "on-demand reload: request after the certificate becomes available signs without restart or manual reload");
    }
    finally
    {
        Directory.Delete(reloadDir, true);
    }
}

// 证书存储来源：私钥指向 Remote KSP 的证书（本机客户端同步来的）不能用，否则签名会转给别的服务端或绕回自己
{
    using var rkRsa = RSA.Create(2048);
    using var rkCert = new CertificateRequest("CN=RemoteKSP Store Loop Probe", rkRsa, HashAlgorithmName.SHA256,
        RSASignaturePadding.Pkcs1).CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(1));
    var rkThumb = rkCert.Thumbprint.ToLowerInvariant();
    try
    {
        CertificateBinder.InstallAndBindCertificate(rkCert.Export(X509ContentType.Cert), rkThumb, StoreLocation.CurrentUser);
        using (var s = new X509Store(StoreName.My, StoreLocation.CurrentUser))
        {
            s.Open(OpenFlags.ReadOnly);
            using var bound = s.Certificates.Find(X509FindType.FindByThumbprint, rkThumb, false)[0];
            Check(bound.HasPrivateKey && !StoreKeySource.IsUsable(bound),
                "store source: Remote KSP-bound certificate (HasPrivateKey=true) is not offered as usable");
        }
        var loopSource = new StoreKeySource(StoreLocation.CurrentUser, rkThumb);
        string? loopError = null;
        try { loopSource.SignHash(new byte[32], "SHA256"); }
        catch (InvalidOperationException ex) { loopError = ex.Message; }
        Check(loopError?.Contains("Remote KSP") == true, "store source: signing with a Remote KSP-bound certificate is refused before any network call");
    }
    finally
    {
        CertificateBinder.UninstallCertificate(rkThumb, StoreLocation.CurrentUser);
    }
}

// 原子写入 + 损坏文件保留：配置读不出来时不能用空列表覆盖原文件
{
    var storeDir = Directory.CreateTempSubdirectory("remoteksp-store-").FullName;
    try
    {
        var target = Path.Combine(storeDir, "data.json");
        SecureStorage.WriteAllTextAtomic(target, "first");
        SecureStorage.WriteAllTextAtomic(target, "second");
        Check(File.ReadAllText(target) == "second" && Directory.GetFiles(storeDir).Length == 1,
            "atomic write: target replaced and no temp files left behind");

        // Windows 不能替换别人开着的文件：读者（GUI、另一线程）只会短暂占用，写入方重试等它关掉
        var briefReader = new FileStream(target, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        var release = Task.Delay(100).ContinueWith(_ => briefReader.Dispose());
        SecureStorage.WriteAllTextAtomic(target, "third");
        await release;
        Check(File.ReadAllText(target) == "third" && Directory.GetFiles(storeDir).Length == 1,
            "atomic write: waits for a brief reader instead of failing, no temp files left");

        const string corruptJson = "{\"Certificates\": [ {\"Id\": \"cert-001\", \"Name\": \"截断";
        var certsFile = Path.Combine(storeDir, "certificates.json");
        File.WriteAllText(certsFile, corruptJson);
        var corruptRegistry = new CertificateRegistry(storeDir);
        var preserved = Directory.GetFiles(storeDir, "certificates.json.corrupt-*");
        Check(corruptRegistry.GetAll().Count == 0 && corruptRegistry.LoadError != null && !File.Exists(certsFile),
            "registry: corrupt certificates.json reported and moved aside");
        Check(preserved.Length == 1 && File.ReadAllText(preserved[0]) == corruptJson,
            "registry: corrupt file preserved byte for byte");
        corruptRegistry.Add(new CertificateEntry { Name = "新证书", Thumbprint = new string('a', 40), SourceType = "test" });
        Check(File.ReadAllText(preserved[0]) == corruptJson && new CertificateRegistry(storeDir).GetAll().Count == 1,
            "registry: saving after corruption writes a new file and leaves the preserved copy untouched");

        var clientsFile = Path.Combine(storeDir, "clients.json");
        File.WriteAllText(clientsFile, "null");
        var corruptClients = new ClientStore(storeDir);
        Check(corruptClients.GetAll().Count == 0 && corruptClients.LoadError != null &&
              Directory.GetFiles(storeDir, "clients.json.corrupt-*").Length == 1,
            "client store: unreadable clients.json reported and preserved instead of overwritten");
    }
    finally
    {
        Directory.Delete(storeDir, true);
    }
}

// 客户端绑定证书：只改动、删除 Remote KSP 自己装的证书。用 CurrentUser\My（不需要管理员）
{
    using var bindKey = RSA.Create(2048);
    X509Certificate2 NewCert(string cn) =>
        new CertificateRequest($"CN={cn}", bindKey, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1)
            .CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(1));
    X509Certificate2? FindInUserStore(string thumb)
    {
        using var s = new X509Store(StoreName.My, StoreLocation.CurrentUser);
        s.Open(OpenFlags.ReadOnly);
        var found = s.Certificates.Find(X509FindType.FindByThumbprint, thumb, false);
        return found.Count > 0 ? found[0] : null;
    }
    void RemoveFromUserStore(string thumb)
    {
        using var s = new X509Store(StoreName.My, StoreLocation.CurrentUser);
        s.Open(OpenFlags.ReadWrite);
        foreach (var c in s.Certificates.Find(X509FindType.FindByThumbprint, thumb, false))
            s.Remove(c);
    }
    bool ThrowsNotManaged(Action action)
    {
        try { action(); return false; }
        catch (CertificateNotManagedException) { return true; }
    }

    // 1) 存储里没有：安装并绑定到 Remote KSP，解绑时删除
    using var fresh = NewCert("RemoteKSP Binder Fresh");
    var freshThumb = fresh.Thumbprint.ToLowerInvariant();
    try
    {
        CertificateBinder.InstallAndBindCertificate(fresh.Export(X509ContentType.Cert), freshThumb, StoreLocation.CurrentUser);
        using (var bound = FindInUserStore(freshThumb))
            Check(bound != null && CertificateBinder.IsManagedByRemoteKsp(bound), "binder: new certificate installed and bound to Remote KSP");
        CertificateBinder.InstallAndBindCertificate(fresh.Export(X509ContentType.Cert), freshThumb, StoreLocation.CurrentUser);
        Check(CertificateBinder.UninstallCertificate(freshThumb, StoreLocation.CurrentUser) && FindInUserStore(freshThumb) == null,
            "binder: re-sync of own certificate allowed, unbind removes it");
    }
    finally { RemoveFromUserStore(freshThumb); }

    // 2) 存储里已有只含公钥的同一张证书（例如服务端为 eToken 导入的 .cer）：不改、不删
    using var publicOnly = NewCert("RemoteKSP Binder PublicOnly");
    var publicThumb = publicOnly.Thumbprint.ToLowerInvariant();
    try
    {
        using (var s = new X509Store(StoreName.My, StoreLocation.CurrentUser))
        {
            s.Open(OpenFlags.ReadWrite);
            s.Add(X509CertificateLoader.LoadCertificate(publicOnly.Export(X509ContentType.Cert)));
        }
        Check(ThrowsNotManaged(() => CertificateBinder.InstallAndBindCertificate(
                publicOnly.Export(X509ContentType.Cert), publicThumb, StoreLocation.CurrentUser)),
            "binder: refuses to take over a pre-existing public-only certificate");
        using (var kept = FindInUserStore(publicThumb))
            Check(kept != null && CertificateBinder.GetKeyProviderName(kept) == null, "binder: pre-existing certificate left without key binding");
        Check(!CertificateBinder.UninstallCertificate(publicThumb, StoreLocation.CurrentUser) && FindInUserStore(publicThumb) != null,
            "binder: unbind keeps a certificate Remote KSP did not install");
    }
    finally { RemoveFromUserStore(publicThumb); }

    // 3) 存储里已有带真实私钥的同一张证书（本机就是服务端的「证书存储」来源）：私钥关联不能被覆盖
    using var withKey = NewCert("RemoteKSP Binder WithKey");
    var keyThumb = withKey.Thumbprint.ToLowerInvariant();
    using var persisted = X509CertificateLoader.LoadPkcs12(withKey.Export(X509ContentType.Pfx, "pw"), "pw",
        X509KeyStorageFlags.PersistKeySet | X509KeyStorageFlags.UserKeySet);
    try
    {
        using (var s = new X509Store(StoreName.My, StoreLocation.CurrentUser))
        {
            s.Open(OpenFlags.ReadWrite);
            s.Add(persisted);
        }
        string? providerBefore;
        using (var before = FindInUserStore(keyThumb))
            providerBefore = CertificateBinder.GetKeyProviderName(before!);
        Check(providerBefore != null && providerBefore != CertificateBinder.ProviderName, "binder: probe certificate has a real key provider");
        Check(ThrowsNotManaged(() => CertificateBinder.InstallAndBindCertificate(
                withKey.Export(X509ContentType.Cert), keyThumb, StoreLocation.CurrentUser)),
            "binder: refuses to rebind a certificate whose private key belongs to another provider");
        using (var after = FindInUserStore(keyThumb))
            Check(after != null && after.HasPrivateKey && CertificateBinder.GetKeyProviderName(after) == providerBefore,
                "binder: original private key binding untouched");
        Check(!CertificateBinder.UninstallCertificate(keyThumb, StoreLocation.CurrentUser) && FindInUserStore(keyThumb) != null,
            "binder: unbind keeps a certificate with a foreign private key");
    }
    finally
    {
        RemoveFromUserStore(keyThumb);
        using var key = persisted.GetRSAPrivateKey();
        if (key is RSACng cng) cng.Key.Delete();
        else if (key is RSACryptoServiceProvider capi) capi.PersistKeyInCsp = false;
    }
}

// 可选：用真实 signtool 执行生成的命令（REMOTEKSP_SIGNTOOL_E2E=<signtool路径>|<指纹>|<目标文件>）
// 只验证命令能被 CMD/SignTool 正确解析并完成签名；不带时间戳，不访问网络
if (Environment.GetEnvironmentVariable("REMOTEKSP_SIGNTOOL_E2E") is { Length: > 0 } e2e)
{
    var parts = e2e.Split('|');
    var e2eResult = SignToolCommandBuilder.Build(new SignToolCommandInput
    {
        Mode = SignToolMode.Discovered, ToolPath = parts[0], Thumbprint = parts[1],
        StoreLocation = StoreLocation.LocalMachine, TargetFile = parts[2], UseTimestamp = false
    });
    Check(e2eResult.IsValid, "E2E: command generated");
    Console.WriteLine("  command: " + e2eResult.Command);
    // 走 GUI「执行签名」用的同一个 SignToolRunner
    var run = await SignToolRunner.RunAsync(e2eResult.Command!, Path.GetDirectoryName(parts[2]));
    Console.WriteLine("  signtool: " + run.Output.Trim().Replace("\n", "\n            "));
    Check(run.Succeeded && run.Output.Contains("Successfully signed"), "E2E: signtool sign exited with 0 via SignToolRunner");
    // 注意：signtool 自身把路径里的非 ASCII 字符输出成空格（原始字节就是 0x20），不是解码问题，
    // 所以这里不比对中文文件名，签名结果以下面的证书校验为准
    using var signer = X509Certificate.CreateFromSignedFile(parts[2]);
    Check(string.Equals(signer.GetCertHashString(), parts[1], StringComparison.OrdinalIgnoreCase),
        "E2E: target file is signed by the selected certificate");
}

Console.WriteLine("\n--- Pairing notifications ---");
// 新请求：事件不带凭据；一阵请求合并成一条通知，两条之间至少隔 MinInterval；已处理的不再提醒
var toastClock = new ManualTimerClock();
var toastPairing = new PairingManager(toastClock);
PairingRequest? raised = null;
toastPairing.RequestCreated += r => raised = r;
var toasts = new List<PairingToastContent>();
var tlsForToast = new string('a', 40);
var notifier = new PairingNotifier(toastPairing, Microsoft.Extensions.Logging.Abstractions.NullLogger.Instance, toastClock,
    c => { toasts.Add(c); return 1; }, () => tlsForToast);
await notifier.StartAsync(default);
var firstPair = toastPairing.CreateRequest("pc-1", "10.0.0.1", "n1");
Check(raised?.RequestId == firstPair.RequestId && raised.GeneratedSecret == null,
    "pairing notification: a new request raises an event without credentials");
Check(toasts.Count == 0, "pairing notification: not shown before the coalescing delay");
toastClock.Advance(PairingNotifier.CoalesceDelay);
Check(toasts.Count == 1 && toasts[0] is { Count: 1, MachineName: "pc-1", IpAddress: "10.0.0.1" } &&
      toasts[0].VerificationCode == Protocol.VerificationCode(tlsForToast, "n1", firstPair.ServerNonce),
    "pairing notification: shows machine, address and the verification code");
toastPairing.CreateRequest("pc-2", "10.0.0.2", "n2");
toastPairing.CreateRequest("pc-3", "10.0.0.3", "n3");
toastClock.Advance(TimeSpan.FromSeconds(10));
Check(toasts.Count == 1, "pairing notification: no second notification within the minimum interval");
toastClock.Advance(notifier.MinInterval);
Check(toasts.Count == 2 && toasts[1] is { Count: 3, MachineName: "pc-3" },
    "pairing notification: a burst becomes one notification with the total and the latest request");
toastPairing.CreateRequest("pc-4", "10.0.0.4", "n4");
foreach (var r in toastPairing.GetPendingRequests())
    toastPairing.Reject(r.RequestId);
toastClock.Advance(notifier.MinInterval);
Check(toasts.Count == 2, "pairing notification: requests handled before it is due are not shown");
var failingNotifier = new PairingNotifier(toastPairing, Microsoft.Extensions.Logging.Abstractions.NullLogger.Instance, toastClock,
    _ => throw new InvalidOperationException("no desktop"), () => null);
await failingNotifier.StartAsync(default);
toastPairing.CreateRequest("pc-5", "10.0.0.5", "n5");
toastClock.Advance(TimeSpan.FromMinutes(1));
Check(toasts.Count == 3 && toastPairing.GetPendingRequests().Count == 1,
    "pairing notification: a failing notifier affects neither pairing nor other subscribers");
await failingNotifier.StopAsync(default);
await notifier.StopAsync(default);

// 机器名来自未认证的客户端：通知 XML 必须转义、去掉控制字符、截断
var hostileName = "<script>&\"x\"\u0007" + new string('y', 100);
var toastXml = System.Xml.Linq.XDocument.Parse(PairingToast.BuildXml(
    new PairingToastContent(1, hostileName, "10.0.0.9", "123456", DateTime.UtcNow)));
var toastTexts = toastXml.Descendants("text").Select(t => t.Value).ToList();
Check(toastTexts.Count == 2 && toastTexts[1].Contains("<script>&\"x\"") && !toastTexts[1].Contains('\u0007') &&
      !toastTexts[1].Contains(new string('y', 100)),
    "pairing notification: the client's machine name is escaped and shortened");
Check(toastXml.Root!.Attribute("launch")!.Value == "remoteksp-server:pairing" &&
      toastXml.Root.Attribute("activationType")!.Value == "protocol",
    "pairing notification: clicking it opens the pairing page link");
Check(PairingToast.BuildXml(new PairingToastContent(4, "a", "b", "c", DateTime.UtcNow)).Contains("4 pairing requests"),
    "pairing notification: several requests show the total");
var toastPayload = new PairingToastContent(2, "名字 \"q\" --open", "::1", "654321", new DateTime(2026, 10, 30, 9, 0, 0, DateTimeKind.Utc));
var encodedPayload = toastPayload.Encode();
Check(encodedPayload.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_') &&
      PairingToastContent.Decode(encodedPayload) == toastPayload,
    "pairing notification: the payload survives the command line unchanged");
Check(PairingToastContent.Decode("!!!") == null && PairingToastContent.Decode(null) == null &&
      PairingToastContent.Decode(new PairingToastContent(0, "a", "b", "c", DateTime.UtcNow).Encode()) == null,
    "pairing notification: malformed payloads are ignored");
Check(UrlScheme.ParseOpenArgument("remoteksp-server:pairing") == UrlScheme.StartPage.PairingRequests &&
      UrlScheme.ParseOpenArgument("REMOTEKSP-SERVER://pairing/") == UrlScheme.StartPage.PairingRequests &&
      UrlScheme.ParseOpenArgument("remoteksp-server:../../evil") == UrlScheme.StartPage.Overview &&
      UrlScheme.ParseOpenArgument("pairing\" --install-service") == UrlScheme.StartPage.Overview &&
      UrlScheme.ParseOpenArgument(null) == UrlScheme.StartPage.Overview,
    "pairing notification: the link only opens known pages");
// 链接里的引号可以注入参数：remoteksp-server:pairing" --uninstall-service --remove-data "
Check(UrlScheme.LinkActivation(["--open", "remoteksp-server:pairing"]) == UrlScheme.StartPage.PairingRequests &&
      UrlScheme.LinkActivation(["--open", "remoteksp-server:pairing", "--uninstall-service", "--remove-data", ""]) == UrlScheme.StartPage.Overview &&
      UrlScheme.LinkActivation(["--install-service", "--open", "x"]) == UrlScheme.StartPage.Overview &&
      UrlScheme.LinkActivation(["--OPEN"]) == UrlScheme.StartPage.Overview &&
      UrlScheme.LinkActivation(["--uninstall-service"]) == null && UrlScheme.LinkActivation([]) == null,
    "pairing notification: a link launch only opens the GUI, injected arguments are ignored");
Check(UserSessionLauncher.WithRunAsInvoker(["Path=C:\\x", "__COMPAT_LAYER=Elevate", "=C:=C:\\", "A=1"]) ==
      "=C:=C:\\\0A=1\0Path=C:\\x\0__COMPAT_LAYER=RunAsInvoker\0\0",
    "pairing notification: the notifier process runs as the user, never elevated");

Console.WriteLine("\n--- Localization ---");
// 每个界面字符串都要有中文翻译，占位符一致；否则 string.Format 会在中文界面上抛异常或丢信息
foreach (var (name, rm) in new[]
{
    ("Shared", SharedStrings.ResourceManager),
    ("Server", RemoteKSP.Server.Localization.Strings.ResourceManager),
    ("Client", RemoteKSP.Client.Localization.Strings.ResourceManager),
})
{
    static Dictionary<string, string> Load(System.Resources.ResourceManager rm, string culture) =>
        rm.GetResourceSet(System.Globalization.CultureInfo.GetCultureInfo(culture), true, false)!
            .Cast<System.Collections.DictionaryEntry>()
            .ToDictionary(e => (string)e.Key, e => (string)e.Value!);
    static string Placeholders(string text) =>
        string.Join(",", System.Text.RegularExpressions.Regex.Matches(text, @"\{\d+").Select(m => m.Value).Distinct().Order());

    var english = Load(rm, "");
    var chinese = Load(rm, UiLanguage.ChineseSimplified);
    var missing = english.Keys.Except(chinese.Keys).ToList();
    var extra = chinese.Keys.Except(english.Keys).ToList();
    var mismatched = english.Keys.Intersect(chinese.Keys)
        .Where(k => Placeholders(english[k]) != Placeholders(chinese[k])).ToList();
    Check(english.Count > 0 && missing.Count == 0 && extra.Count == 0 && mismatched.Count == 0,
        $"localization: {name} zh-Hans has every string with the same placeholders " +
        $"(missing: {string.Join(" ", missing)}; extra: {string.Join(" ", extra)}; placeholders differ: {string.Join(" ", mismatched)})");
}
Check(UiLanguage.Normalize("zh-CN") == UiLanguage.ChineseSimplified && UiLanguage.Normalize("en-US") == UiLanguage.English &&
      UiLanguage.Normalize("fr-FR") == null && UiLanguage.Normalize(null) == null,
    "localization: culture names map to the supported languages");
Check(UiLanguage.Supported.Any(o => o.Code == UiLanguage.SystemLanguage),
    "localization: the Windows display language maps to a supported language");

Console.WriteLine($"\nAll {passed} checks passed (including feature tests).");

partial class Program
{
    [System.Runtime.InteropServices.DllImport("shell32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode, SetLastError = true)]
    static extern IntPtr CommandLineToArgvW(string commandLine, out int count);

    [System.Runtime.InteropServices.DllImport("kernel32.dll")]
    static extern IntPtr LocalFree(IntPtr handle);

    /// <summary>按 Windows 规则拆分命令行（sc.exe 收到的 argv）</summary>
    static string[] SplitCommandLine(string commandLine)
    {
        var argv = CommandLineToArgvW(commandLine, out var count);
        try
        {
            return Enumerable.Range(0, count)
                .Select(i => System.Runtime.InteropServices.Marshal.PtrToStringUni(
                    System.Runtime.InteropServices.Marshal.ReadIntPtr(argv, i * IntPtr.Size))!)
                .ToArray();
        }
        finally { LocalFree(argv); }
    }

    static PinRejectedException Assert(Action action)
    {
        try { action(); }
        catch (PinRejectedException ex) { return ex; }
        throw new Exception("FAIL: expected PinRejectedException");
    }
}

/// <summary>模拟 eToken 硬件：只接受 correct-pin，记录访问次数</summary>
sealed class FakeTokenState(RSA key, byte[] certDer)
{
    private int _calls;
    public int Calls => _calls;
    public volatile bool Locked;
    /// <summary>false = 拔掉了：签名抛 <see cref="Error"/>（默认 NTE_BAD_KEYSET），容器枚举看不到</summary>
    public volatile bool Present = true;
    /// <summary>非 0 时 token 插着但签名返回这个 HRESULT（模拟 CSP 用别的错误码）</summary>
    public volatile int Error;
    /// <summary>只有 PIN 真的被校验时才计数：没插卡、卡忙不算</summary>
    private int _pinChecks;
    public int PinChecks => _pinChecks;
    public void CheckPin() => Interlocked.Increment(ref _pinChecks);
    public RSA Key { get; } = key;
    public byte[] CertDer { get; } = certDer;
    /// <summary>非 null 时访问 token 的请求停在这里，等测试放行（用来在签名途中 Reload）</summary>
    public ManualResetEventSlim? Gate { get; init; }
    public ManualResetEventSlim Entered { get; } = new();
    public void Touch() => Interlocked.Increment(ref _calls);
}

sealed class FakeETokenSource(FakeTokenState token, string thumbprint, string container = "fake-container")
    : ETokenKeySource("Fake CSP", container, thumbprint,
        Microsoft.Extensions.Logging.Abstractions.NullLogger<ETokenKeySource>.Instance)
{
    protected override byte[] SignWithPin(string pin, byte[] hash, HashAlgorithmName hashAlgorithm)
    {
        token.Touch();
        token.Entered.Set();
        token.Gate?.Wait();
        Thread.Sleep(20); // 让并发请求在锁上排队
        if (!token.Present)
            throw new CryptographicException(unchecked((int)0x80090016)); // NTE_BAD_KEYSET：没插卡，没校验 PIN
        if ((uint)token.Error is PinFailure.ScardSharingViolation or PinFailure.ScardTimeout or PinFailure.ScardNoSmartcard)
            throw new CryptographicException(token.Error); // 卡忙/超时/没卡：发生在校验 PIN 之前
        token.CheckPin();
        if (token.Error != 0)
            throw new CryptographicException(token.Error); // 校验了 PIN，但 CSP 返回一个不认识的错误码
        if (token.Locked)
            throw new CryptographicException(unchecked((int)PinFailure.ScardChvBlocked));
        if (pin != "correct-pin")
            throw new CryptographicException(unchecked((int)PinFailure.ScardWrongChv));
        return token.Key.SignHash(hash, hashAlgorithm, RSASignaturePadding.Pkcs1);
    }

    protected override bool IsTokenPresent() => token.Present;

    public override byte[] GetCertificate() => token.CertDer;
}

/// <summary>时间和计时器都手动推进的时钟：Advance 到期时同步触发计时器</summary>
sealed class ManualTimerClock : TimeProvider
{
    private DateTimeOffset _now = DateTimeOffset.FromUnixTimeSeconds(1_800_000_000);
    private readonly List<ManualTimer> _timers = [];
    public override DateTimeOffset GetUtcNow() => _now;

    public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
    {
        var timer = new ManualTimer(this, callback, state, _now + dueTime);
        lock (_timers) _timers.Add(timer);
        return timer;
    }

    public void Advance(TimeSpan value)
    {
        _now += value;
        List<ManualTimer> due;
        lock (_timers)
        {
            due = _timers.Where(t => t.Due <= _now).ToList();
            _timers.RemoveAll(due.Contains);
        }
        foreach (var timer in due)
            timer.Fire();
    }

    private void Remove(ManualTimer timer) { lock (_timers) _timers.Remove(timer); }

    private sealed class ManualTimer(ManualTimerClock clock, TimerCallback callback, object? state, DateTimeOffset due) : ITimer
    {
        public DateTimeOffset Due { get; } = due;
        public void Fire() => callback(state);
        public bool Change(TimeSpan dueTime, TimeSpan period) => false;
        public void Dispose() => clock.Remove(this);
        public ValueTask DisposeAsync() { Dispose(); return ValueTask.CompletedTask; }
    }
}

sealed class TestClock : TimeProvider
{
    private DateTimeOffset _now = DateTimeOffset.FromUnixTimeSeconds(1_800_000_000);
    public override DateTimeOffset GetUtcNow() => _now;
    public void Advance(TimeSpan value) => _now += value;
}
