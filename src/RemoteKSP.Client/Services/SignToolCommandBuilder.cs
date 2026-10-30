using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography.X509Certificates;

namespace RemoteKSP.Client.Services;

/// <summary>SignTool 可执行文件的来源</summary>
public enum SignToolMode
{
    /// <summary>使用自动发现的某个 signtool.exe 完整路径</summary>
    Discovered,
    /// <summary>用户手动指定的 signtool.exe 路径</summary>
    Custom,
    /// <summary>只写 signtool，由运行命令的终端通过 PATH 解析</summary>
    PathCommand
}

/// <summary>生成命令所需的全部输入</summary>
public sealed record SignToolCommandInput
{
    public SignToolMode Mode { get; init; }
    /// <summary>Discovered/Custom 模式下 signtool.exe 的完整路径</summary>
    public string? ToolPath { get; init; }
    public string Thumbprint { get; init; } = "";
    public StoreLocation StoreLocation { get; init; } = StoreLocation.LocalMachine;
    public string StoreName { get; init; } = "My";
    public string TargetFile { get; init; } = "";
    public bool UseTimestamp { get; init; } = true;
    public string TimestampUrl { get; init; } = "";
    public bool Verbose { get; init; }
}

/// <summary>生成结果：成功时 Command 非空；失败时按字段给出原因</summary>
public sealed record SignToolCommandResult(string? Command, IReadOnlyDictionary<string, string> Errors)
{
    public bool IsValid => Command != null;
}

/// <summary>
/// 构造单条 Windows CMD 可解析的 signtool sign 命令。
/// 只做纯字符串构造，不访问文件系统、不运行 signtool，便于单元测试。
/// </summary>
public static class SignToolCommandBuilder
{
    public const string FieldTool = "Tool";
    public const string FieldThumbprint = "Thumbprint";
    public const string FieldTarget = "Target";
    public const string FieldTimestamp = "Timestamp";

    /// <summary>
    /// CMD 在双引号内仍会处理的字符：
    /// " 会结束引号；% 会展开环境变量（交互式 CMD 无法可靠转义）；
    /// ! 在启用延迟展开时会展开；换行/控制字符会切断命令。
    /// 这些字符无法在单条命令里安全表达，遇到即报错而不是生成可能被误解释的命令。
    /// </summary>
    private static readonly char[] UnsafeInQuotes = ['"', '%', '!'];

    public static SignToolCommandResult Build(SignToolCommandInput input)
    {
        var errors = new Dictionary<string, string>();
        var args = new List<string>();

        // 1. 工具
        string? tool = input.Mode switch
        {
            SignToolMode.PathCommand => "signtool",
            _ when string.IsNullOrWhiteSpace(input.ToolPath) => Fail(errors, FieldTool, Strings.St_SelectTool),
            _ => CheckSafe(errors, FieldTool, input.ToolPath!.Trim(), Strings.St_ToolPathLabel)
        };
        if (tool != null && input.Mode != SignToolMode.PathCommand)
        {
            if (!IsAbsoluteWindowsPath(tool))
                tool = Fail(errors, FieldTool, Strings.St_ToolFullPath);
            else
                tool = Quote(tool);
        }

        // 2. 证书：/sha1 是证书 SHA-1 指纹（选择证书），与文件摘要 /fd 无关
        var thumbprint = NormalizeThumbprint(input.Thumbprint);
        if (thumbprint == null)
            Fail(errors, FieldThumbprint, Strings.St_ThumbprintFormat);

        // 3. 目标文件
        string? target = string.IsNullOrWhiteSpace(input.TargetFile)
            ? Fail(errors, FieldTarget, Strings.St_SelectTarget)
            : CheckSafe(errors, FieldTarget, input.TargetFile.Trim(), Strings.St_TargetLabel);
        if (target != null && !IsAbsoluteWindowsPath(target))
            target = Fail(errors, FieldTarget, Strings.St_TargetFullPath);

        // 4. 时间戳
        string? timestamp = null;
        if (input.UseTimestamp)
        {
            // 只校验格式，命令里保留用户原文（Uri.AbsoluteUri 会补尾部斜杠或改写编码）
            var url = input.TimestampUrl.Trim();
            if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https")
                || string.IsNullOrEmpty(uri.Host))
                Fail(errors, FieldTimestamp, Strings.St_TimestampFormat);
            else
                timestamp = CheckSafe(errors, FieldTimestamp, url, Strings.St_TimestampLabel);
        }

        if (errors.Count > 0)
            return new SignToolCommandResult(null, errors);

        args.Add(tool!);
        args.Add("sign");
        if (input.Verbose)
            args.Add("/v");

        // 证书存储：signtool 默认搜索 CurrentUser\My；LocalMachine 需 /sm，非 My 存储需 /s <名称>
        if (input.StoreLocation == StoreLocation.LocalMachine)
            args.Add("/sm");
        if (!string.Equals(input.StoreName, "My", StringComparison.OrdinalIgnoreCase))
        {
            var store = CheckSafe(errors, FieldThumbprint, input.StoreName, Strings.St_StoreLabel);
            if (store == null)
                return new SignToolCommandResult(null, errors);
            args.Add("/s");
            args.Add(QuoteIfNeeded(store));
        }

        args.Add("/sha1");
        args.Add(thumbprint!);
        args.Add("/fd");
        args.Add("SHA256");

        if (timestamp != null)
        {
            args.Add("/tr");
            args.Add(Quote(timestamp));
            args.Add("/td");
            args.Add("SHA256");
        }

        args.Add(Quote(target!));
        return new SignToolCommandResult(string.Join(" ", args), errors);
    }

    /// <summary>去掉空格/冒号等分隔符并转大写；不是 40 位十六进制返回 null</summary>
    public static string? NormalizeThumbprint(string? value)
    {
        if (value == null)
            return null;
        var hex = new string(value.Where(c => !char.IsWhiteSpace(c) && c != ':' && c != '-' && c != '\u200e').ToArray());
        return hex.Length == 40 && hex.All(Uri.IsHexDigit) ? hex.ToUpperInvariant() : null;
    }

    /// <summary>CMD 双引号包裹。调用前必须已通过 <see cref="CheckSafe"/>，因此内容里没有 " % ! 和控制字符</summary>
    public static string Quote(string value) => "\"" + value + "\"";

    private static string QuoteIfNeeded(string value) =>
        value.Any(c => char.IsWhiteSpace(c) || "&|<>^()".Contains(c)) ? Quote(value) : value;

    /// <summary>检查值能否放进 CMD 双引号；不能则记录字段错误并返回 null</summary>
    private static string? CheckSafe(Dictionary<string, string> errors, string field, string value, string label)
    {
        if (value.Any(c => char.IsControl(c)))
            return Fail(errors, field, string.Format(Strings.St_ControlChars, label));
        var bad = value.IndexOfAny(UnsafeInQuotes);
        if (bad >= 0)
            return Fail(errors, field,
                string.Format(Strings.St_UnsafeChar, label, value[bad]));
        // 以反斜杠结尾时，"C:\dir\" 会被 signtool 的参数解析当作转义的引号
        if (value.EndsWith('\\'))
            return Fail(errors, field, string.Format(Strings.St_TrailingBackslash, label));
        return value;
    }

    private static string? Fail(Dictionary<string, string> errors, string field, string message)
    {
        errors.TryAdd(field, message);
        return null;
    }

    /// <summary>盘符路径（C:\...）或 UNC 路径（\\server\share\...）</summary>
    public static bool IsAbsoluteWindowsPath(string path) =>
        (path.Length >= 3 && char.IsAsciiLetter(path[0]) && path[1] == ':' && path[2] == '\\') ||
        (path.StartsWith(@"\\", StringComparison.Ordinal) && path.Length > 2);
}
