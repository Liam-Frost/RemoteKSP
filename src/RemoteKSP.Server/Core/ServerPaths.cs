using System;
using System.IO;

namespace RemoteKSP.Server.Core;

/// <summary>应用程序路径配置</summary>
public static class ServerPaths
{
    /// <summary>数据目录：C:\ProgramData\RemoteKSP</summary>
    public static readonly string DataDir = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
        "RemoteKSP");

    /// <summary>配置文件：config.json</summary>
    public static readonly string ConfigFile = Path.Combine(DataDir, "config.json");

    /// <summary>客户端配对信息：clients.json</summary>
    public static readonly string ClientsFile = Path.Combine(DataDir, "clients.json");

    /// <summary>TLS 证书：server.pfx</summary>
    public static readonly string TlsCertFile = Path.Combine(DataDir, "server.pfx");

    /// <summary>PIN 文件（DPAPI 加密）：pin.dat</summary>
    public static readonly string PinFile = Path.Combine(DataDir, "pin.dat");

    /// <summary>确保数据目录存在；新建时就只允许 SYSTEM 和 Administrators 访问</summary>
    public static void EnsureDataDirectory()
    {
        if (!Directory.Exists(DataDir))
        {
            SecureStorage.EnsureRestrictedDirectory(DataDir);
        }
    }

    /// <summary>服务启动时调用：收紧已有数据目录（旧版本建的目录继承了 Users 读权限）</summary>
    public static void SecureDataDirectory() => SecureStorage.EnsureRestrictedDirectory(DataDir);
}
