using System;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace RemoteKSP.Client.Services;

/// <summary>SignTool 命令生成器的用户偏好设置，保存在 %LOCALAPPDATA%\RemoteKSP\signtool-settings.json</summary>
public sealed class SignToolSettings
{
    private static readonly string SettingsFile =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "RemoteKSP", "signtool-settings.json");

    [JsonConverter(typeof(JsonStringEnumConverter))]
    public SignToolMode Mode { get; set; } = SignToolMode.Discovered;

    public string CustomToolPath { get; set; } = "";
    public string SelectedDiscoveredPath { get; set; } = "";
    public string LastTargetDirectory { get; set; } = "";
    public bool UseTimestamp { get; set; } = true;
    public string TimestampUrl { get; set; } = "http://timestamp.digicert.com";
    public bool Verbose { get; set; }

    public static SignToolSettings Load()
    {
        if (!File.Exists(SettingsFile))
            return new SignToolSettings();
        try
        {
            var json = File.ReadAllText(SettingsFile);
            return JsonSerializer.Deserialize<SignToolSettings>(json) ?? new SignToolSettings();
        }
        catch
        {
            return new SignToolSettings();
        }
    }

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(SettingsFile)!);
            var json = JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(SettingsFile, json);
        }
        catch
        {
            // 保存失败静默忽略，不影响功能
        }
    }
}
