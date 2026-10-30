# Changelog

All notable changes to this project are documented here.
The format follows [Keep a Changelog](https://keepachangelog.com/en/1.1.0/) and the
project uses [Semantic Versioning](https://semver.org/).

The release workflow publishes the section whose heading matches the pushed tag
as the GitHub Release notes, so every release needs a `## [x.y.z] - YYYY-MM-DD`
section here before it is tagged.

## [Unreleased]

Planned as **2.2.0**, the first public release.

### Added
- Published as open source under the MIT License.
- `RemoteKSP.sln` covering every .NET project.
- GitHub Actions: CI on every push / pull request (native build, .NET build,
  security regression tests) and a release workflow that publishes both
  executables with `SHA256SUMS.txt` and build provenance attestations.
- English README (`README.md`) and Chinese README (`README.zh-CN.md`).
- `SECURITY.md` with the vulnerability reporting policy; protocol and data
  protection details in `docs/SECURITY_DESIGN.md`.
- `publish-all.ps1 -Test` runs the regression suite; every build writes `SHA256SUMS.txt`.
