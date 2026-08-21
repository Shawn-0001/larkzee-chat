using System;
using System.Text.Json.Serialization;

namespace LarkzeeChat.Models;

/// <summary>
/// A framed message exchanged by two Larkzee Chat peers.
/// </summary>
/// <remarks>
/// The connection key is never represented by this type and is therefore never
/// serialized onto the wire.  Versioned authentication messages use
/// <see cref="Data"/> and <see cref="PublicKey"/>; post-auth messages use an
/// encrypted envelope with <see cref="Sequence"/> and <see cref="Tag"/>.
/// </remarks>
public sealed class NetworkMessage
{
    [JsonPropertyName("type")]
    public string Type { get; set; } = string.Empty;

    [JsonPropertyName("version")]
    public int? Version { get; set; }

    [JsonPropertyName("sequence")]
    public long? Sequence { get; set; }

    [JsonPropertyName("data")]
    public string? Data { get; set; }

    [JsonPropertyName("tag")]
    public string? Tag { get; set; }

    [JsonPropertyName("publicKey")]
    public string? PublicKey { get; set; }

    [JsonPropertyName("text")]
    public string? Text { get; set; }

    [JsonPropertyName("timestamp")]
    public DateTimeOffset? Timestamp { get; set; }

    [JsonPropertyName("reason")]
    public string? Reason { get; set; }
}
