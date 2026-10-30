using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using Microsoft.Win32;

namespace RemoteKSP.Client.Services;

/// <summary>一个已发现的 signtool.exe</summary>
public sealed record SignToolLocation(string Path, string Source, string? SdkVersion, string? Architecture,
    string? ProductVersion)
{
    /// <summary>下拉框显示：「SDK 10.0.26100.0 · x64」</summary>
    public string DisplayName => string.Join(" · ", new[]
    {
        SdkVersion != null ? $"SDK {SdkVersion}" : Source, Architecture
    }.Where(s => !string.IsNullOrEmpty(s)));
}

/// <summary>
/// 查找本机 signtool.exe：PATH → 注册表登记的 Windows SDK 根目录 bin\&lt;版本&gt;\&lt;架构&gt; → App Certification Kit。
/// 只检查这些固定位置，不扫描整个磁盘；没有安装 SDK 时返回空列表。
/// </summary>
public static class SignToolDiscovery
{
    private const string FileName = "signtool.exe";

    public static IReadOnlyList<SignToolLocation> Discover()
    {
        var results = new List<SignToolLocation>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        void Add(string path, string source, string? sdk, string? arch)
        {
            if (File.Exists(path) && seen.Add(Path.GetFullPath(path)))
                results.Add(new SignToolLocation(path, source, sdk, arch, ReadProductVersion(path)));
        }

        foreach (var path in FindOnPath())
            Add(path, "PATH", null, null);

        foreach (var root in SdkRoots())
        {
            var bin = Path.Combine(root, "bin");
            if (Directory.Exists(bin))
            {
                // 版本目录按数值降序；目录名不是版本号的（如 bin\x64 旧布局）也照常检查
                var versionDirs = Directory.EnumerateDirectories(bin)
                    .Select(d => (dir: d, version: Version.TryParse(Path.GetFileName(d), out var v) ? v : null))
                    .OrderByDescending(x => x.version);
                foreach (var (dir, version) in versionDirs)
                    foreach (var arch in Architectures())
                        Add(Path.Combine(dir, arch, FileName), "Windows SDK", version?.ToString(), arch);
                foreach (var arch in Architectures())
                    Add(Path.Combine(bin, arch, FileName), "Windows SDK", null, arch);
            }
            Add(Path.Combine(root, "App Certification Kit", FileName), "App Certification Kit", null, null);
        }
        return results;
    }

    /// <summary>推荐：与系统架构一致、SDK 版本最高的那个；都没有时取第一个</summary>
    public static SignToolLocation? Recommend(IReadOnlyList<SignToolLocation> found)
    {
        var native = Architectures().First();
        return found.Where(f => f.Architecture == native && f.SdkVersion != null)
                   .OrderByDescending(f => Version.Parse(f.SdkVersion!)).FirstOrDefault()
               ?? found.FirstOrDefault(f => f.Architecture == native)
               ?? found.FirstOrDefault();
    }

    /// <summary>当前 GUI 进程 PATH 中能解析到的 signtool.exe</summary>
    public static IEnumerable<string> FindOnPath() =>
        (Environment.GetEnvironmentVariable("PATH") ?? "")
            .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(d => d.Trim('"'))
            .Where(d => d.Length > 0 && d.IndexOfAny(Path.GetInvalidPathChars()) < 0)
            .Select(d => Path.Combine(d, FileName))
            .Where(File.Exists);

    /// <summary>系统原生架构优先，其次 x64、x86</summary>
    private static IEnumerable<string> Architectures()
    {
        var native = RuntimeInformation.OSArchitecture == Architecture.Arm64 ? "arm64" : "x64";
        return new[] { native, "x64", "x86", "arm64" }.Distinct();
    }

    private static IEnumerable<string> SdkRoots()
    {
        var roots = new[]
        {
            Registry.GetValue(@"HKEY_LOCAL_MACHINE\SOFTWARE\Microsoft\Windows Kits\Installed Roots", "KitsRoot10", null) as string,
            Registry.GetValue(@"HKEY_LOCAL_MACHINE\SOFTWARE\WOW6432Node\Microsoft\Windows Kits\Installed Roots", "KitsRoot10", null) as string,
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "Windows Kits", "10")
        };
        return roots.Where(r => !string.IsNullOrWhiteSpace(r) && Directory.Exists(r))
                    .Select(r => Path.GetFullPath(r!).TrimEnd('\\'))
                    .Distinct(StringComparer.OrdinalIgnoreCase);
    }

    private static string? ReadProductVersion(string path)
    {
        try { return FileVersionInfo.GetVersionInfo(path).ProductVersion; }
        catch { return null; }
    }
}
