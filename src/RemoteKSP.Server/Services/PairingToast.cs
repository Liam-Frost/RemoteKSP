using System;
using System.IO;
using System.Linq;
using System.Security;
using System.Text;
using System.Text.Json;
using Microsoft.Win32;

namespace RemoteKSP.Server.Services;

/// <summary>Windows 通知里显示的配对请求（最新一个，加上待处理总数）</summary>
public sealed record PairingToastContent(int Count, string MachineName, string IpAddress, string VerificationCode,
    DateTime ExpiresAtUtc)
{
    /// <summary>放进命令行的参数：JSON 再 base64url，避免机器名里的引号、空格破坏命令行</summary>
    public string Encode() =>
        Convert.ToBase64String(JsonSerializer.SerializeToUtf8Bytes(this)).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    /// <summary>解析失败或内容越界返回 null</summary>
    public static PairingToastContent? Decode(string? encoded)
    {
        if (string.IsNullOrEmpty(encoded) || encoded.Length > 4096)
            return null;
        try
        {
            var base64 = encoded.Replace('-', '+').Replace('_', '/');
            base64 = base64.PadRight(base64.Length + (4 - base64.Length % 4) % 4, '=');
            var content = JsonSerializer.Deserialize<PairingToastContent>(Convert.FromBase64String(base64));
            return content is { Count: > 0 and < 10000 } ? content : null;
        }
        catch (Exception ex) when (ex is FormatException or JsonException)
        {
            return null;
        }
    }
}

/// <summary>
/// 配对请求的 Windows 通知。由服务在管理员的会话里以该用户身份（不提权）启动
/// RemoteKSP.Server.exe --notify-pairing 来显示：session 0 的服务自己没有桌面。
/// 点击通知通过 remoteksp-server: 链接打开服务端的「配对请求」页。
/// </summary>
public static class PairingToast
{
    public const string AppUserModelId = "RemoteKSP.Server";
    public const string Tag = "pairing";
    public const string OpenPairingUri = UrlScheme.Name + ":pairing";

    /// <summary>客户端提交的机器名不可信：去掉控制字符、限制长度</summary>
    public static string Clean(string? text, int maxLength)
    {
        var cleaned = new string((text ?? "").Where(c => !char.IsControl(c)).ToArray()).Trim();
        return cleaned.Length <= maxLength ? cleaned : cleaned[..maxLength] + "…";
    }

    public static string BuildXml(PairingToastContent content)
    {
        string title, body;
        if (content.Count == 1)
        {
            title = Strings.Toast_PairingTitle;
            body = string.Format(Strings.Toast_PairingBody, Clean(content.MachineName, 64), Clean(content.IpAddress, 64),
                Clean(content.VerificationCode, 16), content.ExpiresAtUtc.ToLocalTime().ToString("HH:mm"));
        }
        else
        {
            title = string.Format(Strings.Toast_PairingsTitle, content.Count);
            body = string.Format(Strings.Toast_PairingsBody, Clean(content.MachineName, 64), Clean(content.IpAddress, 64));
        }

        var uri = SecurityElement.Escape(OpenPairingUri);
        return new StringBuilder()
            .Append($"<toast launch=\"{uri}\" activationType=\"protocol\">")
            .Append("<visual><binding template=\"ToastGeneric\">")
            .Append($"<text>{SecurityElement.Escape(title)}</text>")
            .Append($"<text>{SecurityElement.Escape(body)}</text>")
            .Append("</binding></visual>")
            .Append($"<actions><action content=\"{SecurityElement.Escape(Strings.Toast_Review)}\" ")
            .Append($"arguments=\"{uri}\" activationType=\"protocol\"/></actions>")
            .Append("</toast>")
            .ToString();
    }

    /// <summary>在当前用户会话里显示通知；同一 Tag 的新通知替换旧的，不会堆积</summary>
    public static void Show(PairingToastContent content)
    {
        RegisterAppIdentity();
        WinRtToast.Show(AppUserModelId, BuildXml(content), Tag);
    }

    /// <summary>
    /// 未打包的桌面程序要先登记 AppUserModelID（显示名、图标）才能发通知。
    /// 写在当前用户的 HKCU，不需要管理员权限，也不需要开始菜单快捷方式
    /// </summary>
    private static void RegisterAppIdentity()
    {
        var iconPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "RemoteKSP", "notification.png");
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(iconPath)!);
            using var icon = typeof(PairingToast).Assembly.GetManifestResourceStream("RemoteKSP.Server.Notification.png");
            if (icon != null)
            {
                using var file = File.Create(iconPath);
                icon.CopyTo(file);
            }
        }
        catch (IOException) { /* 图标写不了不影响通知本身 */ }
        catch (UnauthorizedAccessException) { }

        using var key = Registry.CurrentUser.CreateSubKey($@"Software\Classes\AppUserModelId\{AppUserModelId}", true);
        key.SetValue("DisplayName", Strings.Toast_AppName);
        if (File.Exists(iconPath))
            key.SetValue("IconUri", iconPath);
    }
}
