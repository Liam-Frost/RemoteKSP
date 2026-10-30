# Changelog

All notable changes to this project are documented here.
The format follows [Keep a Changelog](https://keepachangelog.com/en/1.1.0/) and the
project uses [Semantic Versioning](https://semver.org/).

The release workflow publishes the section whose heading matches the pushed tag
as the GitHub Release notes, so every release needs a `## [x.y.z] - YYYY-MM-DD`
section here before it is tagged.

## [Unreleased]

## [2.2.0] - 2026-10-30

The first public release.

### Added
- English user interface. Server and client can switch between English and
  Simplified Chinese under **Settings → Language** (default English; takes effect
  after a restart). The service writes its own messages in the language last used
  by the server GUI and answers each client in the client's language.
- Published as open source under the MIT License.
- `RemoteKSP.sln` covering every .NET project.
- GitHub Actions: CI on every push / pull request (native build, .NET build,
  security regression tests) and a release workflow that publishes both
  executables with `SHA256SUMS.txt` and build provenance attestations.
- English README (`README.md`) and Chinese README (`README.zh-CN.md`).
- `SECURITY.md` with the vulnerability reporting policy; protocol and data
  protection details in `docs/SECURITY_DESIGN.md`.
- `publish-all.ps1 -Test` runs the regression suite; every build writes `SHA256SUMS.txt`.

### Changed
- Built on .NET 10 (LTS); Microsoft.Extensions packages updated to 10.0.
  The executables stay self-contained, so no runtime install is needed.

[Unreleased]: https://github.com/Liam-Frost/RemoteKSP/compare/v2.2.0...HEAD
[2.2.0]: https://github.com/Liam-Frost/RemoteKSP/releases/tag/v2.2.0
