using System;
using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;

namespace LarkzeeChat.Networking;

/// <summary>
/// The cryptographic primitives used by one authenticated connection.  The
/// protocol deliberately keeps this type internal: a fresh instance belongs
/// to exactly one TCP connection and is discarded when that connection closes.
/// </summary>
internal static class ProtocolCrypto
{
    internal const int ProtocolVersion = 2;
    internal const int ChallengeLength = 32;
    internal const int ProofLength = 32;
    internal const int PublicKeyMaximumLength = 512;
    internal const int SessionKeyMaterialLength = 72;
    internal const int AesKeyLength = 32;
    internal const int NoncePrefixLength = 4;
    internal const int AesTagLength = 16;
    internal const int AesNonceLength = 12;

    private const string TranscriptDomain = "LarkzeeChat/v2/auth-transcript";
    private const string ClientProofDomain = "LarkzeeChat/v2/client-proof";
    private const string ServerProofDomain = "LarkzeeChat/v2/server-proof";
    private const string KdfDomain = "LarkzeeChat/v2/session-keys";
    private const string EnvelopeDomain = "LarkzeeChat/v2/encrypted-envelope";
    private const string P256Oid = "1.2.840.10045.3.1.7";

    internal static ECDiffieHellman CreateEphemeralKeyAgreement()
    {
        return ECDiffieHellman.Create(ECCurve.NamedCurves.nistP256);
    }

    internal static byte[] ExportPublicKey(ECDiffieHellman keyAgreement)
    {
        ArgumentNullException.ThrowIfNull(keyAgreement);
        byte[] publicKey = keyAgreement.ExportSubjectPublicKeyInfo();
        if (publicKey.Length == 0 || publicKey.Length > PublicKeyMaximumLength)
        {
            CryptographicOperations.ZeroMemory(publicKey);
            throw new CryptographicException("The local P-256 public key has an invalid size.");
        }

        return publicKey;
    }

    internal static bool TryImportPublicKey(
        string? encoded,
        out ECDiffieHellman? publicKeyAgreement,
        out byte[] publicKeyBytes)
    {
        publicKeyAgreement = null;
        publicKeyBytes = Array.Empty<byte>();
        if (string.IsNullOrWhiteSpace(encoded))
        {
            return false;
        }

        try
        {
            byte[] candidate = Convert.FromBase64String(encoded);
            if (candidate.Length == 0 || candidate.Length > PublicKeyMaximumLength)
            {
                CryptographicOperations.ZeroMemory(candidate);
                return false;
            }

            ECDiffieHellman imported = ECDiffieHellman.Create();
            try
            {
                imported.ImportSubjectPublicKeyInfo(candidate, out int bytesRead);
                if (bytesRead != candidate.Length || !IsP256PublicKey(imported))
                {
                    imported.Dispose();
                    CryptographicOperations.ZeroMemory(candidate);
                    return false;
                }

                publicKeyAgreement = imported;
                publicKeyBytes = candidate;
                return true;
            }
            catch
            {
                imported.Dispose();
                CryptographicOperations.ZeroMemory(candidate);
                return false;
            }
        }
        catch (FormatException)
        {
            return false;
        }
        catch (ArgumentException)
        {
            return false;
        }
        catch (CryptographicException)
        {
            return false;
        }
    }

    internal static bool IsP256PublicKey(ECDiffieHellman keyAgreement)
    {
        try
        {
            ECParameters parameters = keyAgreement.ExportParameters(false);
            return string.Equals(parameters.Curve.Oid.Value, P256Oid, StringComparison.Ordinal)
                && parameters.Q.X is { Length: 32 }
                && parameters.Q.Y is { Length: 32 };
        }
        catch (CryptographicException)
        {
            return false;
        }
        catch (ArgumentException)
        {
            return false;
        }
    }

    internal static byte[] CreateTranscriptHash(
        ReadOnlySpan<byte> challenge,
        ReadOnlySpan<byte> serverPublicKey,
        ReadOnlySpan<byte> clientPublicKey)
    {
        byte[] transcript = BuildLengthPrefixedTranscript(
            TranscriptDomain,
            challenge,
            serverPublicKey,
            clientPublicKey);
        try
        {
            return SHA256.HashData(transcript);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(transcript);
        }
    }

    internal static byte[] ComputeClientProof(string connectionKey, ReadOnlySpan<byte> transcriptHash)
    {
        return ComputeRoleProof(connectionKey, ClientProofDomain, transcriptHash);
    }

    internal static byte[] ComputeServerProof(string connectionKey, ReadOnlySpan<byte> transcriptHash)
    {
        return ComputeRoleProof(connectionKey, ServerProofDomain, transcriptHash);
    }

    internal static bool ProofMatches(
        string connectionKey,
        ReadOnlySpan<byte> expectedTranscriptHash,
        ReadOnlySpan<byte> proof,
        bool serverRole)
    {
        byte[] expected = serverRole
            ? ComputeServerProof(connectionKey, expectedTranscriptHash)
            : ComputeClientProof(connectionKey, expectedTranscriptHash);
        try
        {
            return expected.Length == proof.Length
                && CryptographicOperations.FixedTimeEquals(expected, proof);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(expected);
        }
    }

    internal static SessionCrypto DeriveSessionCrypto(
        ECDiffieHellman localKeyAgreement,
        ECDiffieHellman remoteKeyAgreement,
        ReadOnlySpan<byte> transcriptHash,
        bool isOutbound)
    {
        ArgumentNullException.ThrowIfNull(localKeyAgreement);
        ArgumentNullException.ThrowIfNull(remoteKeyAgreement);

        byte[] sharedSecret = Array.Empty<byte>();
        byte[] derivedMaterial = Array.Empty<byte>();
        try
        {
            sharedSecret = localKeyAgreement.DeriveRawSecretAgreement(remoteKeyAgreement.PublicKey);
            if (sharedSecret.Length == 0)
            {
                throw new CryptographicException("The ECDH agreement returned an empty secret.");
            }

            byte[] salt = transcriptHash.ToArray();
            byte[] info = Encoding.UTF8.GetBytes(KdfDomain);
            try
            {
                derivedMaterial = HKDF.DeriveKey(
                    HashAlgorithmName.SHA256,
                    sharedSecret,
                    SessionKeyMaterialLength,
                    salt,
                    info);
            }
            finally
            {
                CryptographicOperations.ZeroMemory(salt);
                CryptographicOperations.ZeroMemory(info);
            }
            if (derivedMaterial.Length != SessionKeyMaterialLength)
            {
                throw new CryptographicException("The session key derivation returned invalid material.");
            }

            return SessionCrypto.Create(derivedMaterial, isOutbound);
        }
        finally
        {
            if (sharedSecret.Length != 0)
            {
                CryptographicOperations.ZeroMemory(sharedSecret);
            }

            if (derivedMaterial.Length != 0)
            {
                CryptographicOperations.ZeroMemory(derivedMaterial);
            }
        }
    }

    internal static byte[] BuildAssociatedData(long sequence)
    {
        if (sequence < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(sequence));
        }

        Span<byte> version = stackalloc byte[sizeof(int)];
        BinaryPrimitives.WriteInt32BigEndian(version, ProtocolVersion);
        Span<byte> sequenceBytes = stackalloc byte[sizeof(long)];
        BinaryPrimitives.WriteInt64BigEndian(sequenceBytes, sequence);
        return BuildLengthPrefixedFields(
            Encoding.UTF8.GetBytes(EnvelopeDomain),
            version.ToArray(),
            sequenceBytes.ToArray());
    }

    private static byte[] ComputeRoleProof(
        string connectionKey,
        string roleDomain,
        ReadOnlySpan<byte> transcriptHash)
    {
        ArgumentNullException.ThrowIfNull(connectionKey);
        byte[] keyBytes = Encoding.UTF8.GetBytes(connectionKey);
        byte[] roleBytes = Encoding.UTF8.GetBytes(roleDomain);
        byte[] transcriptBytes = transcriptHash.ToArray();
        byte[] proofInput = BuildLengthPrefixedFields(roleBytes, transcriptBytes);
        try
        {
            using HMACSHA256 hmac = new(keyBytes);
            return hmac.ComputeHash(proofInput);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(keyBytes);
            CryptographicOperations.ZeroMemory(roleBytes);
            CryptographicOperations.ZeroMemory(transcriptBytes);
            CryptographicOperations.ZeroMemory(proofInput);
        }
    }

    private static byte[] BuildLengthPrefixedTranscript(
        string domain,
        ReadOnlySpan<byte> challenge,
        ReadOnlySpan<byte> serverPublicKey,
        ReadOnlySpan<byte> clientPublicKey)
    {
        byte[] domainBytes = Encoding.UTF8.GetBytes(domain);
        byte[] challengeBytes = challenge.ToArray();
        byte[] serverPublicKeyBytes = serverPublicKey.ToArray();
        byte[] clientPublicKeyBytes = clientPublicKey.ToArray();
        Span<byte> version = stackalloc byte[sizeof(int)];
        BinaryPrimitives.WriteInt32BigEndian(version, ProtocolVersion);
        byte[] versionBytes = version.ToArray();
        try
        {
            return BuildLengthPrefixedFields(
                domainBytes,
                versionBytes,
                challengeBytes,
                serverPublicKeyBytes,
                clientPublicKeyBytes);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(domainBytes);
            CryptographicOperations.ZeroMemory(versionBytes);
            CryptographicOperations.ZeroMemory(challengeBytes);
            CryptographicOperations.ZeroMemory(serverPublicKeyBytes);
            CryptographicOperations.ZeroMemory(clientPublicKeyBytes);
        }
    }

    private static byte[] BuildLengthPrefixedFields(params byte[][] fields)
    {
        int totalLength = 0;
        foreach (byte[] field in fields)
        {
            checked
            {
                totalLength += sizeof(int) + field.Length;
            }
        }

        byte[] result = new byte[totalLength];
        int offset = 0;
        foreach (byte[] field in fields)
        {
            BinaryPrimitives.WriteInt32BigEndian(result.AsSpan(offset, sizeof(int)), field.Length);
            offset += sizeof(int);
            field.AsSpan().CopyTo(result.AsSpan(offset, field.Length));
            offset += field.Length;
        }

        return result;
    }
}

/// <summary>
/// Directional AES-256-GCM state for one authenticated connection.
/// </summary>
internal sealed class SessionCrypto : IDisposable
{
    private byte[] _sendKey;
    private byte[] _receiveKey;
    private byte[] _sendNoncePrefix;
    private byte[] _receiveNoncePrefix;
    private AesGcm? _sendCipher;
    private AesGcm? _receiveCipher;
    private int _disposed;

    private SessionCrypto(ReadOnlySpan<byte> material, bool isOutbound)
    {
        if (material.Length != ProtocolCrypto.SessionKeyMaterialLength)
        {
            throw new ArgumentException("The derived session material has an invalid length.", nameof(material));
        }

        ReadOnlySpan<byte> clientToServerKey = material[..ProtocolCrypto.AesKeyLength];
        ReadOnlySpan<byte> serverToClientKey = material.Slice(ProtocolCrypto.AesKeyLength, ProtocolCrypto.AesKeyLength);
        ReadOnlySpan<byte> clientToServerNonce = material.Slice(ProtocolCrypto.AesKeyLength * 2, ProtocolCrypto.NoncePrefixLength);
        ReadOnlySpan<byte> serverToClientNonce = material.Slice(ProtocolCrypto.AesKeyLength * 2 + ProtocolCrypto.NoncePrefixLength, ProtocolCrypto.NoncePrefixLength);

        _sendKey = (isOutbound ? clientToServerKey : serverToClientKey).ToArray();
        _receiveKey = (isOutbound ? serverToClientKey : clientToServerKey).ToArray();
        _sendNoncePrefix = (isOutbound ? clientToServerNonce : serverToClientNonce).ToArray();
        _receiveNoncePrefix = (isOutbound ? serverToClientNonce : clientToServerNonce).ToArray();

        try
        {
            _sendCipher = new AesGcm(_sendKey, ProtocolCrypto.AesTagLength);
            _receiveCipher = new AesGcm(_receiveKey, ProtocolCrypto.AesTagLength);
        }
        catch
        {
            CryptographicOperations.ZeroMemory(_sendKey);
            CryptographicOperations.ZeroMemory(_receiveKey);
            CryptographicOperations.ZeroMemory(_sendNoncePrefix);
            CryptographicOperations.ZeroMemory(_receiveNoncePrefix);
            throw;
        }
    }

    internal static SessionCrypto Create(ReadOnlySpan<byte> material, bool isOutbound)
    {
        return new SessionCrypto(material, isOutbound);
    }

    internal void Encrypt(
        long sequence,
        ReadOnlySpan<byte> plaintext,
        out byte[] ciphertext,
        out byte[] tag)
    {
        ThrowIfDisposed();
        if (sequence < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(sequence));
        }

        ciphertext = new byte[plaintext.Length];
        tag = new byte[ProtocolCrypto.AesTagLength];
        byte[] nonce = BuildNonce(_sendNoncePrefix, sequence);
        byte[] associatedData = ProtocolCrypto.BuildAssociatedData(sequence);
        try
        {
            _sendCipher!.Encrypt(nonce, plaintext, ciphertext, tag, associatedData);
        }
        catch
        {
            CryptographicOperations.ZeroMemory(ciphertext);
            CryptographicOperations.ZeroMemory(tag);
            ciphertext = Array.Empty<byte>();
            tag = Array.Empty<byte>();
            throw;
        }
        finally
        {
            CryptographicOperations.ZeroMemory(nonce);
            CryptographicOperations.ZeroMemory(associatedData);
        }
    }

    internal bool TryDecrypt(
        long sequence,
        ReadOnlySpan<byte> ciphertext,
        ReadOnlySpan<byte> tag,
        out byte[] plaintext)
    {
        plaintext = Array.Empty<byte>();
        if (Volatile.Read(ref _disposed) != 0 || sequence < 0 || tag.Length != ProtocolCrypto.AesTagLength)
        {
            return false;
        }

        byte[] candidate = new byte[ciphertext.Length];
        byte[] nonce = BuildNonce(_receiveNoncePrefix, sequence);
        byte[] associatedData = ProtocolCrypto.BuildAssociatedData(sequence);
        try
        {
            _receiveCipher!.Decrypt(nonce, ciphertext, tag, candidate, associatedData);
            plaintext = candidate;
            return true;
        }
        catch (CryptographicException)
        {
            CryptographicOperations.ZeroMemory(candidate);
            return false;
        }
        catch (ObjectDisposedException)
        {
            CryptographicOperations.ZeroMemory(candidate);
            return false;
        }
        finally
        {
            CryptographicOperations.ZeroMemory(nonce);
            CryptographicOperations.ZeroMemory(associatedData);
        }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        _sendCipher?.Dispose();
        _receiveCipher?.Dispose();
        _sendCipher = null;
        _receiveCipher = null;
        CryptographicOperations.ZeroMemory(_sendKey);
        CryptographicOperations.ZeroMemory(_receiveKey);
        CryptographicOperations.ZeroMemory(_sendNoncePrefix);
        CryptographicOperations.ZeroMemory(_receiveNoncePrefix);
    }

    private static byte[] BuildNonce(ReadOnlySpan<byte> prefix, long sequence)
    {
        byte[] nonce = new byte[ProtocolCrypto.AesNonceLength];
        prefix.CopyTo(nonce);
        BinaryPrimitives.WriteInt64BigEndian(nonce.AsSpan(ProtocolCrypto.NoncePrefixLength), sequence);
        return nonce;
    }

    private void ThrowIfDisposed()
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
    }
}
