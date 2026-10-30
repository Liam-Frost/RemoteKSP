using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text;
using System.Threading;

namespace RemoteKSP.Server.Core;

/// <summary>
/// 数据目录的权限和落盘。目录里有 DPAPI LocalMachine 加密的 PIN、客户端密钥和 TLS 私钥，
/// LocalMachine 范围本机任何进程都能解密，所以只能靠 ACL 挡住普通用户。
/// </summary>
public static class SecureStorage
{
    private static readonly SecurityIdentifier LocalSystem = new(WellKnownSidType.LocalSystemSid, null);
    private static readonly SecurityIdentifier Administrators = new(WellKnownSidType.BuiltinAdministratorsSid, null);

    /// <summary>只允许 SYSTEM 和 Administrators 访问的目录 ACL（不继承上级）</summary>
    private static DirectorySecurity RestrictedDirectorySecurity()
    {
        var security = new DirectorySecurity();
        security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
        foreach (var sid in new[] { LocalSystem, Administrators })
        {
            security.AddAccessRule(new FileSystemAccessRule(sid, FileSystemRights.FullControl,
                InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit,
                PropagationFlags.None, AccessControlType.Allow));
        }
        return security;
    }

    /// <summary>
    /// 创建或收紧目录：只允许 SYSTEM 和 Administrators，去掉从 ProgramData 继承的 Users 读权限；
    /// 已有的子文件和子目录重置为只继承这两条。
    /// 普通用户能在 ProgramData 下建目录：目录本身或其中任何一项是联接/符号链接时拒绝，
    /// 否则 PIN 和密钥会写进对方指定的目录，收紧的也只是链接本身
    /// </summary>
    public static void EnsureRestrictedDirectory(string path)
    {
        var dir = new DirectoryInfo(path);
        if (!dir.Exists)
        {
            // 不 return：检查和收紧照常做一遍，堵住"检查不存在之后、创建之前被人抢先建好"的情况
            try { dir.Create(RestrictedDirectorySecurity()); }
            catch (IOException) when (Directory.Exists(path)) { }
            dir.Refresh();
        }

        RejectReparsePoint(dir);

        // 已经收紧过（普通用户进不来，也改不了 ACL）：里面的东西都是 SYSTEM 或管理员放的，不必再扫一遍
        if (IsRestricted(dir))
            return;

        var entries = CollectEntries(dir);

        // 子项先重置为只继承（之后收紧父目录时随之生效），旧版本或手工加的显式 ACE 一并清掉。
        // 旧版本目录允许 Users 新建文件，所有者总能改 ACL，所以子项所有者也改成 Administrators
        foreach (var entry in entries)
        {
            FileSystemSecurity inherit = entry is DirectoryInfo ? new DirectorySecurity() : new FileSecurity();
            inherit.SetAccessRuleProtection(isProtected: false, preserveInheritance: false);
            SetAccessControlWithOwner(entry, inherit);
        }

        SetAccessControlWithOwner(dir, RestrictedDirectorySecurity());

        // 收紧之后再确认一次：中途被换成链接，或 ACL 没生效，都不能继续用这个目录
        dir.Refresh();
        RejectReparsePoint(dir);
        if (!IsRestricted(dir))
            throw new UnauthorizedAccessException(string.Format(Strings.Secure_RestrictFailed, dir.FullName));
    }

    /// <summary>
    /// 目录 ACL 已不继承上级、只允许 SYSTEM 和 Administrators，且所有者不是普通用户能冒充的身份
    /// （SYSTEM、Administrators 或当前进程自己——服务就是 SYSTEM，GUI 以管理员运行）
    /// </summary>
    private static bool IsRestricted(DirectoryInfo dir)
    {
        var security = dir.GetAccessControl();
        if (!security.AreAccessRulesProtected)
            return false;
        var allowed = security.GetAccessRules(true, true, typeof(SecurityIdentifier))
            .Cast<FileSystemAccessRule>()
            .All(r => r.IdentityReference.Equals(LocalSystem) || r.IdentityReference.Equals(Administrators));
        var owner = security.GetOwner(typeof(SecurityIdentifier));
        var trustedOwner = owner != null && (owner.Equals(LocalSystem) || owner.Equals(Administrators) ||
            owner.Equals(WindowsIdentity.GetCurrent().User));
        return allowed && trustedOwner;
    }

    /// <summary>写入 ACL 并把所有者改成 Administrators；没有权限改所有者（未提权的测试进程）时只写 ACL</summary>
    private static void SetAccessControlWithOwner(FileSystemInfo entry, FileSystemSecurity security)
    {
        void Apply(FileSystemSecurity s)
        {
            if (entry is DirectoryInfo d) d.SetAccessControl((DirectorySecurity)s);
            else ((FileInfo)entry).SetAccessControl((FileSecurity)s);
        }

        security.SetOwner(Administrators);
        try
        {
            Apply(security);
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or InvalidOperationException)
        {
            // 同一组规则去掉所有者部分重试：新建一个对象，复制 DACL 保护状态和规则
            FileSystemSecurity aclOnly = entry is DirectoryInfo ? new DirectorySecurity() : new FileSecurity();
            aclOnly.SetAccessRuleProtection(security.AreAccessRulesProtected, preserveInheritance: false);
            foreach (FileSystemAccessRule rule in security.GetAccessRules(true, false, typeof(SecurityIdentifier)))
                aclOnly.AddAccessRule(rule);
            Apply(aclOnly);
        }
    }

    /// <summary>逐层列出所有子项（含隐藏、系统文件）；遇到链接立即拒绝，不进入它指向的目录</summary>
    private static List<FileSystemInfo> CollectEntries(DirectoryInfo root)
    {
        var result = new List<FileSystemInfo>();
        var pending = new Stack<DirectoryInfo>();
        pending.Push(root);
        var options = new EnumerationOptions { AttributesToSkip = 0, IgnoreInaccessible = false };
        while (pending.Count > 0)
        {
            foreach (var entry in pending.Pop().EnumerateFileSystemInfos("*", options))
            {
                RejectReparsePoint(entry);
                result.Add(entry);
                if (entry is DirectoryInfo sub)
                    pending.Push(sub);
            }
        }
        return result;
    }

    private static void RejectReparsePoint(FileSystemInfo entry)
    {
        if ((entry.Attributes & FileAttributes.ReparsePoint) != 0)
        {
            throw new UnauthorizedAccessException(string.Format(Strings.Secure_ReparsePoint, entry.FullName));
        }
    }

    /// <summary>先写同目录的临时文件并刷到磁盘，再整体替换目标；中途断电不会留下写了一半的目标文件</summary>
    public static void WriteAllBytesAtomic(string path, byte[] data)
    {
        var temp = $"{path}.{Guid.NewGuid():N}.tmp";
        try
        {
            using (var stream = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None,
                       4096, FileOptions.WriteThrough))
            {
                stream.Write(data);
                stream.Flush(flushToDisk: true);
            }

            // 另一个进程正好在读目标文件时替换会失败，稍等重试
            for (var attempt = 1; ; attempt++)
            {
                try
                {
                    File.Move(temp, path, overwrite: true);
                    return;
                }
                catch (Exception ex) when (attempt < 5 && ex is IOException or UnauthorizedAccessException)
                {
                    Thread.Sleep(50 * attempt);
                }
            }
        }
        finally
        {
            if (File.Exists(temp))
                File.Delete(temp);
        }
    }

    public static void WriteAllTextAtomic(string path, string text) =>
        WriteAllBytesAtomic(path, new UTF8Encoding(false).GetBytes(text));

    /// <summary>读取时允许别的进程同时替换这个文件（FileShare.Delete），不会挡住原子写入</summary>
    public static string ReadAllText(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete);
        using var reader = new StreamReader(stream, Encoding.UTF8);
        return reader.ReadToEnd();
    }

    /// <summary>解析不了的配置文件改名保留，下一次保存不会覆盖它；返回保留后的路径</summary>
    public static string PreserveCorruptFile(string path)
    {
        var backup = $"{path}.corrupt-{DateTime.Now:yyyyMMdd-HHmmss}";
        File.Move(path, backup, overwrite: true);
        return backup;
    }
}
