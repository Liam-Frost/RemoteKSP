using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Security.AccessControl;
using System.Security.Principal;

namespace RemoteKSP.Server.Services;

/// <summary>
/// 服务程序文件：固定装在 %ProgramFiles%\RemoteKSP。服务以 SYSTEM 运行，exe 所在目录谁能写，
/// 谁就能以 SYSTEM 执行代码，所以不能直接用下载/发布目录（普通用户通常可写）里的 exe 注册服务
/// </summary>
public static class ServiceProgram
{
    public const string ExeName = "RemoteKSP.Server.exe";

    public static string InstallDir { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "RemoteKSP");

    public static string InstalledExe { get; } = Path.Combine(InstallDir, ExeName);

    /// <summary>sc.exe create/config 的 binPath 参数：exe 路径单独加引号，避免带空格的路径被拆开</summary>
    public static string BinPathArgument(string exePath) => $"binPath= \"\\\"{exePath}\\\" --service\"";

    /// <summary>从服务的 ImagePath 取出 exe 路径；兼容旧版本注册的不带引号的路径</summary>
    public static string? ExeFromImagePath(string? imagePath)
    {
        if (string.IsNullOrWhiteSpace(imagePath))
            return null;
        var value = Environment.ExpandEnvironmentVariables(imagePath.Trim());
        if (value.StartsWith('"'))
        {
            var end = value.IndexOf('"', 1);
            return end > 1 ? value[1..end] : null;
        }
        var exe = value.IndexOf(".exe", StringComparison.OrdinalIgnoreCase);
        return exe > 0 ? value[..(exe + 4)] : value;
    }

    /// <summary>普通用户（Users、Authenticated Users、Everyone 等）能否在这个文件或目录里写入、删除或改权限</summary>
    public static bool WritableByNonAdmins(FileSystemInfo entry)
    {
        FileSystemSecurity security = entry is DirectoryInfo d ? d.GetAccessControl() : ((FileInfo)entry).GetAccessControl();
        var risky = new[]
        {
            WellKnownSidType.WorldSid, WellKnownSidType.BuiltinUsersSid, WellKnownSidType.AuthenticatedUserSid,
            WellKnownSidType.InteractiveSid, WellKnownSidType.BuiltinGuestsSid, WellKnownSidType.AnonymousSid
        }.Select(t => new SecurityIdentifier(t, null)).ToHashSet();
        const FileSystemRights write = FileSystemRights.WriteData | FileSystemRights.AppendData |
            FileSystemRights.Delete | FileSystemRights.DeleteSubdirectoriesAndFiles |
            FileSystemRights.ChangePermissions | FileSystemRights.TakeOwnership;
        const int genericWriteOrAll = 0x40000000 | 0x10000000; // GENERIC_WRITE | GENERIC_ALL

        return security.GetAccessRules(true, true, typeof(SecurityIdentifier)).Cast<FileSystemAccessRule>()
            .Any(r => r.AccessControlType == AccessControlType.Allow &&
                      risky.Contains((SecurityIdentifier)r.IdentityReference) &&
                      ((r.FileSystemRights & write) != 0 || ((int)r.FileSystemRights & genericWriteOrAll) != 0));
    }

    /// <summary>
    /// 把 <paramref name="sourceExe"/> 部署到 <paramref name="targetExe"/>：先完整复制到同目录的临时文件，
    /// 再把旧文件改名让位（运行中的 exe 不能覆盖，但可以改名），最后换上新文件；任何一步失败都恢复旧文件。
    /// 目标目录普通用户可写时拒绝部署
    /// </summary>
    public static void DeployExecutable(string sourceExe, string targetExe)
    {
        var target = Path.GetFullPath(targetExe);
        if (string.Equals(Path.GetFullPath(sourceExe), target, StringComparison.OrdinalIgnoreCase))
            return; // GUI 本身就是从安装目录运行的

        var dir = Directory.CreateDirectory(Path.GetDirectoryName(target)!);
        if (WritableByNonAdmins(dir))
            throw new UnauthorizedAccessException($"{dir.FullName} 普通用户可写，服务以 SYSTEM 运行，不能把服务程序放在这里");

        var temp = target + ".new";
        var aside = $"{target}.old-{DateTime.Now:yyyyMMddHHmmssfff}";
        File.Copy(sourceExe, temp, overwrite: true);
        var movedAside = false;
        try
        {
            if (File.Exists(target))
            {
                File.Move(target, aside);
                movedAside = true;
            }
            File.Move(temp, target);
        }
        catch
        {
            if (movedAside && !File.Exists(target))
                File.Move(aside, target);
            if (File.Exists(temp))
                File.Delete(temp);
            throw;
        }

        if (movedAside)
            DeleteOrScheduleAtReboot(aside);
    }

    /// <summary>两个 exe 是否同一个构建：先比大小和版本信息（含提交哈希），再逐字节比较</summary>
    public static bool SameBuild(string a, string b)
    {
        var fa = new FileInfo(a);
        var fb = new FileInfo(b);
        if (!fa.Exists || !fb.Exists || fa.Length != fb.Length)
            return false;
        if (FileVersionInfo.GetVersionInfo(a).ProductVersion != FileVersionInfo.GetVersionInfo(b).ProductVersion)
            return false;

        const int chunk = 1 << 20;
        using var sa = fa.OpenRead();
        using var sb = fb.OpenRead();
        var bufA = new byte[chunk];
        var bufB = new byte[chunk];
        int read;
        while ((read = sa.ReadAtLeast(bufA, chunk, throwOnEndOfStream: false)) > 0)
        {
            sb.ReadExactly(bufB, 0, read);
            if (!bufA.AsSpan(0, read).SequenceEqual(bufB.AsSpan(0, read)))
                return false;
        }
        return true;
    }

    /// <summary>
    /// 删除安装目录里的服务程序和升级留下的旧文件；目录空了就删掉目录。
    /// 当前 GUI 正是从这里运行的 exe 删不掉，保留并返回它的路径
    /// </summary>
    public static string? RemoveInstalledProgram(string installDir, string? runningExe)
    {
        if (!Directory.Exists(installDir))
            return null;

        string? kept = null;
        foreach (var file in Directory.EnumerateFiles(installDir, ExeName + "*"))
        {
            if (runningExe != null && string.Equals(Path.GetFullPath(file), Path.GetFullPath(runningExe),
                    StringComparison.OrdinalIgnoreCase))
            {
                kept = file;
                continue;
            }
            DeleteOrScheduleAtReboot(file);
        }

        if (!Directory.EnumerateFileSystemEntries(installDir).Any())
            Directory.Delete(installDir);
        return kept;
    }

    /// <summary>
    /// 删除数据目录。先检查整棵树：有联接或符号链接就拒绝，绝不跟着链接删到别的目录里去
    /// </summary>
    public static void RemoveDataDirectory(string dataDir)
    {
        var root = new DirectoryInfo(dataDir);
        if (!root.Exists)
            return;
        if ((root.Attributes & FileAttributes.ReparsePoint) != 0)
            throw new UnauthorizedAccessException($"{root.FullName} 是联接或符号链接，未删除数据目录。请管理员确认后手动处理");

        var entries = new System.Collections.Generic.List<FileSystemInfo> { root };
        var pending = new System.Collections.Generic.Stack<DirectoryInfo>();
        pending.Push(root);
        var options = new EnumerationOptions { AttributesToSkip = 0, IgnoreInaccessible = false };
        while (pending.Count > 0)
        {
            foreach (var entry in pending.Pop().EnumerateFileSystemInfos("*", options))
            {
                entries.Add(entry);
                if (entry is DirectoryInfo sub && (sub.Attributes & FileAttributes.ReparsePoint) == 0)
                    pending.Push(sub);
            }
        }
        if (entries.FirstOrDefault(e => (e.Attributes & FileAttributes.ReparsePoint) != 0) is { } link)
            throw new UnauthorizedAccessException($"{link.FullName} 是联接或符号链接，未删除数据目录。请管理员确认后手动处理");

        // 先删文件，再从最深的目录往上删
        foreach (var file in entries.OfType<FileInfo>())
        {
            file.Attributes = FileAttributes.Normal;
            file.Delete();
        }
        foreach (var dir in entries.OfType<DirectoryInfo>().OrderByDescending(d => d.FullName.Length))
            dir.Delete();
    }

    private const int MOVEFILE_DELAY_UNTIL_REBOOT = 0x4;

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool MoveFileEx(string existing, string? newName, int flags);

    /// <summary>删除文件；被占用（例如旧 exe 仍有进程在用）时安排重启后删除</summary>
    private static void DeleteOrScheduleAtReboot(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            MoveFileEx(path, null, MOVEFILE_DELAY_UNTIL_REBOOT);
        }
    }
}
