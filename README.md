<p align="center">
  <img src="src/Shared/LOGO/RemoteKSP_LOGO.png" alt="RemoteKSP" width="128">
</p>

<h1 align="center">RemoteKSP</h1>

<p align="center">
  Sign code on any Windows machine with a certificate whose private key never leaves your signing server.
</p>

<p align="center">
  <a href="../../actions/workflows/ci.yml"><img src="../../actions/workflows/ci.yml/badge.svg" alt="CI"></a>
  <a href="../../releases/latest"><img src="https://img.shields.io/github/v/release/Liam-Frost/RemoteKSP" alt="Release"></a>
  <a href="LICENSE"><img src="https://img.shields.io/badge/license-MIT-blue.svg" alt="MIT License"></a>
</p>

<p align="center"><b>English</b> · <a href="README.zh-CN.md">简体中文</a></p>

---

RemoteKSP is a Windows **Key Storage Provider (KSP)** that forwards CNG signing
operations to a remote server. Plug your eToken / HSM into one machine, pair
your build agents and workstations with it, and `signtool`, MSBuild, Office,
Acrobat or any other CNG-aware application can sign as if the certificate were
local — without copying the private key or the token anywhere.

```
signtool / any CNG application
        │  NCryptSignHash
        ▼
RemoteKSP_x64.dll / RemoteKSP_x86.dll   (Native KSP on the client)
        │  HTTPS, pinned TLS certificate, HMAC-SHA256 + nonce
        ▼
RemoteKSP.Server                         (Windows service on the signing machine)
        │
        ▼
eToken · certificate store · PFX         (the real private key)
```

Only the hash travels over the network; the signature comes back and is verified
locally against the certificate's public key before it is returned.

## Features

**Server** (`RemoteKSP.Server.exe`, runs as a Windows service)
- Key sources: SafeNet eToken, Windows certificate store, PFX file, and a public-key-only test certificate
- Pairing by approval, with a verification code derived from the TLS fingerprint and both nonces
- Per-client certificate permissions and revocation
- PIN protection: a wrong PIN, a locked token or an unrecognized error disables the
  certificate after a **single** attempt, so the token is never locked by retries
- Secrets protected with DPAPI in a data directory restricted to SYSTEM and Administrators
- Activity log, in-place upgrade, custom HTTPS port, TLS certificate regeneration

**Client** (`RemoteKSP.Client.exe`)
- Pair with one or more servers; each has its own credentials and pinned TLS fingerprint
- Sync shared certificates into `LocalMachine\My` with the private key bound to Remote KSP
- Install / update / remove the Native KSP DLL (x64 and x86) in one click
- Live server status and a `signtool` command builder

**Native KSP** (C++, ~25 KB)
- Implements the CNG KSP interface, PKCS#1 signatures with SHA-1/256/384/512
- Checks the server certificate fingerprint before any request header is sent

Both apps are available in **English** and **Simplified Chinese** (Settings → Language).

## Requirements

- Windows 10 / 11 or Windows Server 2016+ (x64). The KSP DLL is available for x64 and x86 applications.
- Administrator rights on both machines (service install, KSP registration, `LocalMachine` store).
- For eToken: SafeNet Authentication Client on the server.

Both executables are self-contained single files; no .NET runtime needs to be installed.

## Download and verify

Download `RemoteKSP.Server-<version>.exe` and `RemoteKSP.Client-<version>.exe`
from the [latest release](../../releases/latest).

The binaries are **not Authenticode-signed** (the project has no publisher
certificate yet), so SmartScreen may warn on first run. Verify what you downloaded
instead:

```powershell
# 1. Compare with SHA256SUMS.txt from the same release
Get-FileHash .\RemoteKSP.Server-v2.2.0.exe -Algorithm SHA256

# 2. Or verify the build provenance (proves it was built by this repository's workflow)
gh attestation verify .\RemoteKSP.Server-v2.2.0.exe --repo Liam-Frost/RemoteKSP
```

## Quick start

### 1. Set up the server (the machine with the eToken)

Run `RemoteKSP.Server.exe` as Administrator and click **Install service** on the
Overview page. The program is copied to `C:\Program Files\RemoteKSP\` and the
`RemoteKSP` service starts automatically on boot, listening on HTTPS port 8443
(a firewall rule is added). Unattended alternative:

```powershell
.\RemoteKSP.Server.exe --install-service
.\RemoteKSP.Server.exe --uninstall-service [--remove-data]
```

Then add a certificate under **Certificates → Add certificate**:

| Source | Use it for | Notes |
|---|---|---|
| **eToken** | Hardware tokens (recommended) | Import the public `.cer` into `LocalMachine\My` first (`certutil -addstore My cert.cer`), click *Detect eToken* for the container, enter the thumbprint and PIN. A test signature validates the PIN before it is saved. |
| **Certificate store** | Software keys already in Windows | Lists only certificates with a usable private key. |
| **PFX file** | Software keys in a file | Copied into the data directory; password protected with DPAPI. |
| **Test certificate** | Trying out pairing | Public key only — signing with it is refused. |

Tick **Share with clients** so paired clients can sync it.

### 2. Pair a client

1. Run `RemoteKSP.Client.exe` as Administrator and click **Install KSP**.
2. **Servers → Add server**: enter the server address and port.
3. Check that the verification code shown on both machines matches.
4. Approve the request on the server under **Pairing requests**.
5. Shared certificates are synced into `LocalMachine\My` automatically.

### 3. Sign

```powershell
signtool sign /sm /sha1 <thumbprint> /fd SHA256 /tr http://timestamp.digicert.com /td SHA256 app.exe
```

`/sm` selects the machine store. The client's **Certificates** page can generate
and run this command for you. Any other CNG-aware application can simply pick the
certificate.

## Security model in brief

- **Server identity:** the self-signed TLS certificate is pinned at pairing time after
  you compare the verification code; afterwards the client and the KSP DLL refuse
  any other certificate, plain HTTP and redirects.
- **Request authentication:** every request carries an HMAC-SHA256 over method,
  path, timestamp, algorithm, client ID, body and a random nonce. Timestamps must be
  within ±5 minutes; replayed nonces are rejected atomically.
- **Authorization:** each client can be limited to specific certificates and revoked at any time.
- **Secrets at rest:** PINs, PFX passwords, HMAC secrets and the TLS key are DPAPI-protected
  in `C:\ProgramData\RemoteKSP`, which is restricted to SYSTEM and Administrators.

RemoteKSP is designed for **trusted internal networks**. Do not expose the server
port to the internet. Details: [docs/SECURITY_DESIGN.md](docs/SECURITY_DESIGN.md) (Chinese).
To report a vulnerability, see [SECURITY.md](SECURITY.md).

## Building from source

Requirements: Windows, .NET 10 SDK, Visual Studio 2022 with the *Desktop development with C++* workload.

```powershell
.\publish-all.ps1                    # native DLLs + both single-file executables → Publish\
.\publish-all.ps1 -Version 2.2.0     # stamp a version into the executables
.\publish-all.ps1 -Test              # also build and run the regression tests
```

Open `RemoteKSP.sln` in Visual Studio for development. See [docs/BUILD.md](docs/BUILD.md)
for details, the project layout and how releases are made.

## Documentation

| Document | Contents |
|---|---|
| [docs/BUILD.md](docs/BUILD.md) | Building, testing, releasing |
| [docs/SECURITY_DESIGN.md](docs/SECURITY_DESIGN.md) | Protocol, data protection, regression test coverage (Chinese) |
| [docs/Archive/ETOKEN_PIN_ERROR_CHECK.md](docs/Archive/ETOKEN_PIN_ERROR_CHECK.md) | Verifying real eToken error codes (Chinese) |
| [CHANGELOG.md](CHANGELOG.md) | Release history |


## Contributing

Issues and pull requests are welcome. Please run `.\publish-all.ps1 -Test` before
opening a pull request, and add a regression check to `tests/SecurityRegression`
for any behavior change in authentication, pairing or key handling. Security issues
go through [SECURITY.md](SECURITY.md), not public issues.

Translations are welcome too: see [docs/BUILD.md](docs/BUILD.md#translations) for where the UI
text lives and how to add a language.

## License

[MIT](LICENSE)
