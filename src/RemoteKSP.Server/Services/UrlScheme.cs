using System;
using System.Linq;
using Microsoft.Win32;

namespace RemoteKSP.Server.Services;

/// <summary>
/// remoteksp-server: 链接，点击配对通知时用它打开服务端 GUI。安装服务时登记在 HKLM，指向
/// Program Files 里的服务程序（只有管理员能改），卸载时删除。网页等任何地方都能触发这个链接，
/// 所以它只接受固定的几个页面名，而且程序要管理员权限，打开前总会有 UAC 提示
/// </summary>
public static class UrlScheme
{
    public const string Name = "remoteksp-server";
    private const string KeyPath = @"Software\Classes\" + Name;

    public static void Register(string exePath)
    {
        using var key = Registry.LocalMachine.CreateSubKey(KeyPath, true);
        key.SetValue("", "URL:RemoteKSP Server");
        key.SetValue("URL Protocol", "");
        using (var icon = key.CreateSubKey("DefaultIcon", true))
            icon.SetValue("", $"\"{exePath}\",0");
        using var command = key.CreateSubKey(@"shell\open\command", true);
        command.SetValue("", $"\"{exePath}\" --open \"%1\"");
    }

    public static void Unregister() => Registry.LocalMachine.DeleteSubKeyTree(KeyPath, false);

    /// <summary>GUI 启动后要打开的页面</summary>
    public enum StartPage { Overview, PairingRequests }

    /// <summary>
    /// 命令行里有 --open 就是链接启动，返回要打开的页面；否则为 null。登记的命令是 "exe" --open "%1"，
    /// 正常只有这两个参数；多出来的参数只可能是链接里的引号注入的，这时连页面也不认，只打开概览页
    /// </summary>
    public static StartPage? LinkActivation(string[] args)
    {
        if (!args.Contains("--open", StringComparer.OrdinalIgnoreCase))
            return null;
        return args.Length == 2 && string.Equals(args[0], "--open", StringComparison.OrdinalIgnoreCase)
            ? ParseOpenArgument(args[1])
            : StartPage.Overview;
    }

    /// <summary>解析 --open 的参数；不认识的一律回到概览页</summary>
    public static StartPage ParseOpenArgument(string? argument)
    {
        if (string.IsNullOrEmpty(argument))
            return StartPage.Overview;
        var target = argument.StartsWith(Name + ":", StringComparison.OrdinalIgnoreCase)
            ? argument[(Name.Length + 1)..]
            : argument;
        return target.Trim('/').Equals("pairing", StringComparison.OrdinalIgnoreCase)
            ? StartPage.PairingRequests
            : StartPage.Overview;
    }
}
