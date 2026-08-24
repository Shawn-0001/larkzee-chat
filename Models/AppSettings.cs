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

    /// <summary>
    /// The peer's eight-character connection code. It is protected by
    /// SettingsService and therefore never serialized as plaintext.
    /// </summary>
    [JsonIgnore]
    public string RemoteConnectionCode { get; set; } = string.Empty;
}
