using System;
using System.IO;
using System.Reflection;

namespace RemoteKSP.Client.Core;

/// <summary>KSP DLL 管理</summary>
public static class KspInstaller
{
    private static readonly string System32 = Environment.GetFolderPath(Environment.SpecialFolder.System);
    private static readonly string SysWow64 = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "SysWOW64");

    /// <summary>检查是否已安装</summary>
    public static bool IsInstalled()
    {
        var x64Path = Path.Combine(System32, "RemoteKSP.dll");
        var x86Path = Path.Combine(SysWow64, "RemoteKSP.dll");
        return File.Exists(x64Path) && File.Exists(x86Path);
    }

    /// <summary>两种架构的已安装 DLL 必须与当前客户端内嵌版本一致。</summary>
    public static bool IsUpToDate()
    {
        var assembly = Assembly.GetExecutingAssembly();
        return SameContent(assembly, "RemoteKSP.Client.RemoteKSP_x64.dll", Path.Combine(System32, "RemoteKSP.dll"))
            && SameContent(assembly, "RemoteKSP.Client.RemoteKSP_x86.dll", Path.Combine(SysWow64, "RemoteKSP.dll"));
    }

    private static bool SameContent(Assembly assembly, string resourceName, string path)
    {
        if (!File.Exists(path))
            return false;
        using var resource = assembly.GetManifestResourceStream(resourceName);
        if (resource == null)
            return false;
        using var file = File.OpenRead(path);
        return System.Security.Cryptography.SHA256.HashData(resource)
            .AsSpan().SequenceEqual(System.Security.Cryptography.SHA256.HashData(file));
    }

    /// <summary>安装 KSP DLL</summary>
    public static void Install()
    {
        // 从资源提取 DLL
        var assembly = Assembly.GetExecutingAssembly();

        ExtractAndInstall(assembly, "RemoteKSP.Client.RemoteKSP_x64.dll",
            Path.Combine(System32, "RemoteKSP.dll"));

        ExtractAndInstall(assembly, "RemoteKSP.Client.RemoteKSP_x86.dll",
            Path.Combine(SysWow64, "RemoteKSP.dll"));

        // 注册 KSP
        RegisterKsp(Path.Combine(System32, "RemoteKSP.dll"));
    }

    /// <summary>卸载 KSP DLL</summary>
    public static void Uninstall()
    {
        // 注销 KSP
        UnregisterKsp(Path.Combine(System32, "RemoteKSP.dll"));

        // 删除 DLL
        var x64Path = Path.Combine(System32, "RemoteKSP.dll");
        var x86Path = Path.Combine(SysWow64, "RemoteKSP.dll");

        if (File.Exists(x64Path))
            File.Delete(x64Path);

        if (File.Exists(x86Path))
            File.Delete(x86Path);
    }

    private static void ExtractAndInstall(Assembly assembly, string resourceName, string targetPath)
    {
        using var stream = assembly.GetManifestResourceStream(resourceName);
        if (stream == null)
            throw new InvalidOperationException($"Resource not found: {resourceName}");

        // 升级时旧 DLL 可能正被某个进程加载：无法覆盖，但可以改名让位
        if (File.Exists(targetPath))
        {
            try
            {
                File.Delete(targetPath);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                File.Move(targetPath, $"{targetPath}.{DateTime.Now:yyyyMMddHHmmss}.old");
            }
        }

        using var fileStream = new FileStream(targetPath, FileMode.Create, FileAccess.Write);
        stream.CopyTo(fileStream);
    }

    private static void RegisterKsp(string dllPath)
    {
        var regsvr32 = Path.Combine(Path.GetDirectoryName(dllPath)!, "regsvr32.exe");
        var psi = new System.Diagnostics.ProcessStartInfo
        {
            FileName = regsvr32,
            Arguments = $"/s \"{dllPath}\"",
            UseShellExecute = false,
            CreateNoWindow = true
        };

        using var process = System.Diagnostics.Process.Start(psi);
        process?.WaitForExit();

        if (process?.ExitCode != 0)
            throw new InvalidOperationException("KSP registration failed");
    }

    private static void UnregisterKsp(string dllPath)
    {
        var regsvr32 = Path.Combine(Path.GetDirectoryName(dllPath)!, "regsvr32.exe");
        var psi = new System.Diagnostics.ProcessStartInfo
        {
            FileName = regsvr32,
            Arguments = $"/s /u \"{dllPath}\"",
            UseShellExecute = false,
            CreateNoWindow = true
        };

        using var process = System.Diagnostics.Process.Start(psi);
        process?.WaitForExit();
    }
}
