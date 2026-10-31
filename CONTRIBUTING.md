# Contributing to RemoteKSP

Thanks for your interest in improving RemoteKSP. Bug reports, translations, documentation
fixes and code are all welcome. Issues and pull requests can be written in English or Chinese.

By taking part you agree to follow the [Code of Conduct](CODE_OF_CONDUCT.md).

## Security issues first

RemoteKSP controls access to code-signing keys. **Never report a vulnerability in a public
issue, discussion or pull request** — follow [SECURITY.md](SECURITY.md) instead.

## Reporting bugs and requesting features

- Check the [latest release](../../releases/latest) and existing issues first.
- Use the issue forms; they ask for the component, version and Windows version we need.
- Remove thumbprints, hostnames, IP addresses, API keys and PINs from logs before posting.
- Setup questions are better asked in [Discussions](../../discussions).

## Development setup

You need Windows 10/11, the .NET 10 SDK and Visual Studio 2022 or Build Tools with the
**Desktop development with C++** workload.
[docs/BUILD.md](docs/BUILD.md) has the full requirements and explains how the pieces fit
together. The short version:

```powershell
.\publish-all.ps1 -Test    # build native DLLs, Client and Server, then run the regression tests
```

The regression suite in `tests/SecurityRegression` does not need administrator rights,
does not install the KSP and does not touch real keys.

## Pull requests

1. For anything larger than a small fix, open an issue first so we can agree on the approach.
2. Branch from `master` and keep each pull request to one topic.
3. Make sure `.\publish-all.ps1 -Test` passes; CI runs the same command and must be green
   before merging.
4. Add a line under `[Unreleased]` in [CHANGELOG.md](CHANGELOG.md) for user-visible changes.
5. Pull requests are squash-merged, so the PR title becomes the commit message. Write it in
   the imperative and, if you like, with a [Conventional Commits](https://www.conventionalcommits.org/)
   prefix: `fix: retry pairing after the server restarts`.

### Code style

- Follow the style of the surrounding code and keep the build free of new compiler warnings.
- Prefer small, explicit code over abstractions, especially in authentication, pairing and the
  native KSP DLL. Security-relevant changes should come with a regression check in
  `tests/SecurityRegression`.
- Update [docs/SECURITY_DESIGN.md](docs/SECURITY_DESIGN.md) when you change how
  authentication, pairing, key access or network exposure works.

### UI text and translations

All UI text lives in `.resx` files with English as the neutral language. When you add or change
a string, update the matching `*.zh-Hans.resx` as well, or say in the pull request that the
translation is missing. The regression suite fails if a translation lacks a string or its
`{0}` placeholders differ from English.

New languages are welcome; [docs/BUILD.md](docs/BUILD.md#translations) lists the files to add.

## License

By contributing you agree that your contributions are licensed under the
[MIT License](LICENSE) of this project.
