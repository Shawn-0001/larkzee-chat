using System.Text.Json.Serialization;

namespace LarkzeeChat.Models;

/// <summary>
/// User preferences that are safe to keep for the next launch.
/// Password properties are protected by SettingsService before serialization.
/// </summary>
public sealed class AppSettings
{
    public string RemoteIp { get; set; } = string.Empty;

    [JsonIgnore]
    public string LocalPassword { get; set; } = string.Empty;

    [JsonIgnore]
    public string RemotePassword { get; set; } = string.Empty;
}
