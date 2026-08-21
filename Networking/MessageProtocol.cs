using System;
using System.Buffers.Binary;
using System.IO;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using LarkzeeChat.Models;

namespace LarkzeeChat.Networking;

internal static class MessageProtocol
{
    internal const int MaxMessageBytes = 64 * 1024;
    // Leave room for the encrypted envelope, JSON property names and Base64
    // expansion.  A normal 8,000-character message remains well below this
    // cap while a local oversized send is rejected before any network write.
    internal const int MaxEncryptedInnerBytes = 45 * 1024;

    internal const string AuthChallenge = "auth_challenge";
    internal const string AuthResponse = "auth_response";
    internal const string AuthOk = "auth_ok";
    internal const string AuthFailed = "auth_failed";
    internal const string RateLimited = "rate_limited";
    internal const string Busy = "busy";
    internal const string Chat = "chat";
    internal const string Encrypted = "encrypted";
    internal const string Ping = "ping";
    internal const string Pong = "pong";
    internal const string Disconnect = "disconnect";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    internal static NetworkMessage Create(string type)
    {
        return new NetworkMessage { Type = type };
    }

    internal static NetworkMessage CreateVersioned(string type)
    {
        return new NetworkMessage
        {
            Type = type,
            Version = ProtocolCrypto.ProtocolVersion
        };
    }

    internal static NetworkMessage CreateEncrypted(
        SessionCrypto crypto,
        NetworkMessage inner,
        long sequence)
    {
        ArgumentNullException.ThrowIfNull(crypto);
        ArgumentNullException.ThrowIfNull(inner);

        byte[] plaintext = SerializeInnerMessage(inner);
        if (plaintext.Length > MaxEncryptedInnerBytes)
        {
            throw new InvalidDataException("The encrypted inner message is too large.");
        }

        byte[] ciphertext = Array.Empty<byte>();
        byte[] tag = Array.Empty<byte>();
        try
        {
            crypto.Encrypt(sequence, plaintext, out ciphertext, out tag);
            NetworkMessage envelope = new()
            {
                Type = Encrypted,
                Version = ProtocolCrypto.ProtocolVersion,
                Sequence = sequence,
                Data = Convert.ToBase64String(ciphertext),
                Tag = Convert.ToBase64String(tag)
            };

            byte[] envelopeBody = JsonSerializer.SerializeToUtf8Bytes(envelope, JsonOptions);
            if (envelopeBody.Length <= 0 || envelopeBody.Length > MaxMessageBytes)
            {
                throw new InvalidDataException("The encrypted network message is too large.");
            }

            return envelope;
        }
        finally
        {
            CryptographicOperations.ZeroMemory(plaintext);
            if (ciphertext.Length != 0)
            {
                CryptographicOperations.ZeroMemory(ciphertext);
            }

            if (tag.Length != 0)
            {
                CryptographicOperations.ZeroMemory(tag);
            }
        }
    }

    internal static bool TryDecryptEncrypted(
        SessionCrypto crypto,
        NetworkMessage envelope,
        long expectedSequence,
        out NetworkMessage? inner)
    {
        ArgumentNullException.ThrowIfNull(crypto);
        inner = null;
        if (!string.Equals(envelope.Type, Encrypted, StringComparison.OrdinalIgnoreCase)
            || envelope.Version != ProtocolCrypto.ProtocolVersion
            || envelope.Sequence is not long sequence
            || sequence < 0
            || sequence != expectedSequence
            || envelope.Text is not null
            || envelope.Timestamp is not null
            || envelope.Reason is not null
            || envelope.PublicKey is not null
            || !TryDecodeBase64(envelope.Data, out byte[] ciphertext))
        {
            return false;
        }

        byte[] tag = Array.Empty<byte>();
        byte[] plaintext = Array.Empty<byte>();
        try
        {
            if (!TryDecodeBase64Exact(envelope.Tag, ProtocolCrypto.AesTagLength, out tag)
                || ciphertext.Length == 0 || ciphertext.Length > MaxEncryptedInnerBytes
                || !crypto.TryDecrypt(sequence, ciphertext, tag, out plaintext))
            {
                return false;
            }

            try
            {
                inner = JsonSerializer.Deserialize<NetworkMessage>(plaintext, JsonOptions);
            }
            catch (JsonException)
            {
                return false;
            }

            if (inner is null
                || string.IsNullOrWhiteSpace(inner.Type)
                || inner.Version is not null
                || inner.Sequence is not null
                || inner.Tag is not null
                || inner.PublicKey is not null)
            {
                inner = null;
                return false;
            }

            return true;
        }
        finally
        {
            if (ciphertext.Length != 0)
            {
                CryptographicOperations.ZeroMemory(ciphertext);
            }

            if (tag.Length != 0)
            {
                CryptographicOperations.ZeroMemory(tag);
            }

            if (plaintext.Length != 0)
            {
                CryptographicOperations.ZeroMemory(plaintext);
            }
        }
    }

    internal static async Task WriteMessageAsync(
        Stream stream,
        NetworkMessage message,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(stream);
        ArgumentNullException.ThrowIfNull(message);

        if (string.IsNullOrWhiteSpace(message.Type))
        {
            throw new InvalidDataException("A network message must have a type.");
        }

        byte[] body = JsonSerializer.SerializeToUtf8Bytes(message, JsonOptions);
        if (body.Length is <= 0 or > MaxMessageBytes)
        {
            throw new InvalidDataException("The network message is too large.");
        }

        byte[] frame = new byte[sizeof(int) + body.Length];
        BinaryPrimitives.WriteInt32BigEndian(frame.AsSpan(0, sizeof(int)), body.Length);
        body.AsSpan().CopyTo(frame.AsSpan(sizeof(int)));

        // NetworkStream writes the supplied memory as one logical stream
        // operation.  Message sends are additionally serialized per session
        // by ChatSessionManager so two frames can never interleave.
        await stream.WriteAsync(frame.AsMemory(), cancellationToken).ConfigureAwait(false);
    }

    internal static byte[] SerializeInnerMessage(NetworkMessage message)
    {
        ArgumentNullException.ThrowIfNull(message);
        if (string.IsNullOrWhiteSpace(message.Type))
        {
            throw new InvalidDataException("A network message must have a type.");
        }

        byte[] body = JsonSerializer.SerializeToUtf8Bytes(message, JsonOptions);
        if (body.Length is <= 0 or > MaxMessageBytes)
        {
            CryptographicOperations.ZeroMemory(body);
            throw new InvalidDataException("The network message is too large.");
        }

        return body;
    }

    /// <summary>
    /// Reads one complete framed message.  A clean EOF before the next frame
    /// starts is represented by null; a partial frame is a protocol error.
    /// </summary>
    internal static async Task<NetworkMessage?> ReadMessageAsync(
        Stream stream,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(stream);

        byte[] header = new byte[sizeof(int)];
        int headerBytes = await ReadExactlyAsync(stream, header, cancellationToken)
            .ConfigureAwait(false);
        if (headerBytes == 0)
        {
            return null;
        }

        if (headerBytes != header.Length)
        {
            throw new EndOfStreamException("The message length header was incomplete.");
        }

        int length = BinaryPrimitives.ReadInt32BigEndian(header);
        if (length is <= 0 or > MaxMessageBytes)
        {
            throw new InvalidDataException("The network message length is invalid.");
        }

        byte[] body = new byte[length];
        int bodyBytes = await ReadExactlyAsync(stream, body, cancellationToken)
            .ConfigureAwait(false);
        if (bodyBytes != body.Length)
        {
            throw new EndOfStreamException("The message body was incomplete.");
        }

        try
        {
            NetworkMessage? message = JsonSerializer.Deserialize<NetworkMessage>(body, JsonOptions);
            if (message is null || string.IsNullOrWhiteSpace(message.Type))
            {
                throw new InvalidDataException("The network message is empty or has no type.");
            }

            return message;
        }
        catch (JsonException exception)
        {
            throw new InvalidDataException("The network message is not valid JSON.", exception);
        }
    }

    private static async Task<int> ReadExactlyAsync(
        Stream stream,
        Memory<byte> buffer,
        CancellationToken cancellationToken)
    {
        int total = 0;
        while (total < buffer.Length)
        {
            int read = await stream.ReadAsync(buffer[total..], cancellationToken)
                .ConfigureAwait(false);
            if (read == 0)
            {
                return total;
            }

            total += read;
        }

        return total;
    }

    private static bool TryDecodeBase64(
        string? value,
        out byte[] decoded)
    {
        decoded = Array.Empty<byte>();
        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        try
        {
            decoded = Convert.FromBase64String(value);
            return decoded.Length > 0;
        }
        catch (FormatException)
        {
            return false;
        }
    }

    private static bool TryDecodeBase64Exact(
        string? value,
        int expectedLength,
        out byte[] decoded)
    {
        if (!TryDecodeBase64(value, out decoded) || decoded.Length != expectedLength)
        {
            if (decoded.Length != 0)
            {
                CryptographicOperations.ZeroMemory(decoded);
            }

            decoded = Array.Empty<byte>();
            return false;
        }

        return true;
    }
}
