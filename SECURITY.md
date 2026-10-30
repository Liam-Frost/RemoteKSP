# Security Policy

RemoteKSP moves code-signing operations across the network, so security reports
are taken seriously and handled before any other kind of issue.

## Supported versions

Only the latest release receives security fixes. Please reproduce issues on the
latest release before reporting.

| Version | Supported |
|---------|-----------|
| Latest release | ✅ |
| Older releases | ❌ — upgrade first |

## Reporting a vulnerability

**Do not open a public issue, discussion or pull request for a vulnerability.**

Report it privately through GitHub:
**Security → Report a vulnerability** on this repository
([direct link](../../security/advisories/new)).

Please include:

- The affected component (Server, Client, Native KSP DLL) and version
- Steps to reproduce, or a proof of concept
- The impact you believe it has (e.g. signing without authorization, credential
  disclosure, privilege escalation to SYSTEM)
- Any suggested fix, if you have one

You can write in English or Chinese.

## What to expect

- Acknowledgement within **5 business days**
- An initial assessment within **14 days**
- A fix or mitigation plan agreed with you before anything is disclosed
- Credit in the release notes and advisory, unless you prefer to stay anonymous

We follow coordinated disclosure: a GitHub Security Advisory is published together
with the fixed release.

## Scope

In scope:

- Bypassing HMAC request authentication, replay protection or TLS pinning
- Obtaining signatures for certificates a client is not authorized to use
- Disclosure of eToken PINs, PFX passwords, client HMAC secrets or the TLS key
- Local privilege escalation through the Windows service or its data directory
- Memory-safety issues in the Native KSP DLL

Out of scope:

- Attacks that already require Administrator or SYSTEM on the server machine
- Denial of service against a server deliberately exposed to the public internet
  (RemoteKSP is designed for trusted internal networks)
- Issues in third-party CSPs, eToken middleware, or Windows itself

## Security design

The protocol, data protection and threat model are documented in
[docs/SECURITY_DESIGN.md](docs/SECURITY_DESIGN.md) (Chinese).
