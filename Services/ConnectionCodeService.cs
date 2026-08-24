using System.Collections.ObjectModel;
using System.Net;
using System.Security.Cryptography;
using System.Text;

namespace LarkzeeChat.Services;

/// <summary>
/// Encodes an RFC1918 IPv4 address and a short PIN as the eight-character
/// connection code shared by the settings UI. The code is intentionally a
/// compact credential/transport hint, not a replacement for the encrypted
/// authenticated session established after connecting.
/// </summary>
public static class ConnectionCodeService
{
    public const string Alphabet = "abcdefghjklmnpqrstuvwxyz23456789";
    public const int CodeLength = 8;
    public const int DataSymbolCount = 7;
    public const int PinLimit = 1000;

    private const int Gf32ReductionPolynomial = 0x05;
    private const int Gf32PrimitiveElement = 0x02;
    private const ulong TenNetworkSize = 1UL << 24;
    private const ulong SeventeenTwoNetworkSize = 1UL << 20;
    private const ulong OneNineTwoNetworkSize = 1UL << 16;
    private const ulong PrivateAddressCount = TenNetworkSize + SeventeenTwoNetworkSize + OneNineTwoNetworkSize;
    private const string PasswordDomain = "LarkzeeChat/1.0.2/connection-code:";

    // These are alpha^0 through alpha^6 in GF(32), with x^5+x^2+1 as the
    // reduction polynomial. Keeping the values explicit makes the wire
    // format stable across versions and easy to audit.
    private static readonly byte[] CheckWeights = [1, 2, 4, 8, 16, 5, 10];
    private static readonly IReadOnlyList<byte> PrimitivePowerCycle =
        new ReadOnlyCollection<byte>(BuildPrimitivePowerCycle());

    private static readonly UTF8Encoding StrictUtf8 =
        new(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);

    /// <summary>
    /// The seven non-zero powers used by the check symbol. This is exposed
    /// for diagnostics/tests so a future change cannot silently alter the
    /// check-code contract.
    /// </summary>
    public static IReadOnlyList<byte> Gf32PowerCycle => PrimitivePowerCycle;

    public static ConnectionCodeInfo Generate(IPAddress address)
    {
        if (!TryGetPrivateIpIndex(address, out ulong addressIndex))
        {
            throw new ArgumentException("Only RFC1918 IPv4 addresses are supported.", nameof(address));
        }

        int pin = RandomNumberGenerator.GetInt32(PinLimit);
        string code = Encode(addressIndex, pin);
        return new ConnectionCodeInfo(
            code,
            NormalizeAddress(address),
            pin,
            DeriveAuthenticationPassword(code));
    }

    public static bool TryGenerate(
        IPAddress? address,
        out ConnectionCodeInfo result,
        out ConnectionCodeFailureReason failureReason)
    {
        result = ConnectionCodeInfo.Empty;
        failureReason = ConnectionCodeFailureReason.None;
        if (address is null || !TryGetPrivateIpIndex(address, out _))
        {
            failureReason = ConnectionCodeFailureReason.UnsupportedAddress;
            return false;
        }

        result = Generate(address);
        return true;
    }

    /// <summary>
    /// Decodes a code after trimming and lower-casing it. Uppercase input is
    /// accepted for convenience, while all output is normalized lowercase.
    /// </summary>
    public static bool TryDecode(
        string? code,
        out ConnectionCodeInfo result,
        out ConnectionCodeFailureReason failureReason)
    {
        result = ConnectionCodeInfo.Empty;
        failureReason = ConnectionCodeFailureReason.None;

        if (string.IsNullOrWhiteSpace(code))
        {
            failureReason = ConnectionCodeFailureReason.Empty;
            return false;
        }

        string normalized = code.Trim().ToLowerInvariant();
        if (normalized.Length != CodeLength)
        {
            failureReason = ConnectionCodeFailureReason.InvalidLength;
            return false;
        }

        Span<byte> symbols = stackalloc byte[CodeLength];
        for (int index = 0; index < normalized.Length; index++)
        {
            int value = Alphabet.IndexOf(normalized[index]);
            if (value < 0)
            {
                failureReason = ConnectionCodeFailureReason.InvalidCharacter;
                return false;
            }

            symbols[index] = (byte)value;
        }

        byte expectedCheck = ComputeCheckSymbol(symbols[..DataSymbolCount]);
        if (symbols[DataSymbolCount] != expectedCheck)
        {
            failureReason = ConnectionCodeFailureReason.ChecksumMismatch;
            return false;
        }

        ulong payload = 0;
        for (int index = 0; index < DataSymbolCount; index++)
        {
            payload = (payload << 5) | symbols[index];
        }

        if (payload >= PrivateAddressCount * PinLimit)
        {
            failureReason = ConnectionCodeFailureReason.InvalidPayload;
            return false;
        }

        ulong addressIndex = payload / PinLimit;
        int pin = (int)(payload % PinLimit);
        if (!TryGetPrivateIpAddress(addressIndex, out IPAddress address))
        {
            // This should be unreachable after the range check, but keep the
            // decoder fail-closed if the dense mapping is ever changed.
            failureReason = ConnectionCodeFailureReason.InvalidPayload;
            return false;
        }

        result = new ConnectionCodeInfo(
            normalized,
            address,
            pin,
            DeriveAuthenticationPassword(normalized));
        return true;
    }

    public static string DeriveAuthenticationPassword(string normalizedCode)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(normalizedCode);
        string normalized = normalizedCode.Trim().ToLowerInvariant();
        byte[] input = StrictUtf8.GetBytes(PasswordDomain + normalized);
        byte[] digest = SHA256.HashData(input);
        try
        {
            return Convert.ToBase64String(digest)
                .TrimEnd('=')
                .Replace('+', '-')
                .Replace('/', '_');
        }
        finally
        {
            CryptographicOperations.ZeroMemory(input);
            CryptographicOperations.ZeroMemory(digest);
        }
    }

    public static bool TryGetPrivateIpIndex(IPAddress? address, out ulong addressIndex)
    {
        addressIndex = 0;
        if (address is null)
        {
            return false;
        }

        IPAddress ipv4 = NormalizeAddress(address);
        if (ipv4.AddressFamily != System.Net.Sockets.AddressFamily.InterNetwork)
        {
            return false;
        }

        byte[] bytes = ipv4.GetAddressBytes();
        if (bytes[0] == 10)
        {
            addressIndex = ((ulong)bytes[1] << 16)
                | ((ulong)bytes[2] << 8)
                | bytes[3];
            return true;
        }

        if (bytes[0] == 172 && bytes[1] is >= 16 and <= 31)
        {
            addressIndex = TenNetworkSize
                + ((ulong)(bytes[1] - 16) << 16)
                + ((ulong)bytes[2] << 8)
                + bytes[3];
            return true;
        }

        if (bytes[0] == 192 && bytes[1] == 168)
        {
            addressIndex = TenNetworkSize + SeventeenTwoNetworkSize
                + ((ulong)bytes[2] << 8)
                + bytes[3];
            return true;
        }

        return false;
    }

    public static bool TryGetPrivateIpAddress(ulong addressIndex, out IPAddress address)
    {
        address = IPAddress.None;
        if (addressIndex >= PrivateAddressCount)
        {
            return false;
        }

        if (addressIndex < TenNetworkSize)
        {
            address = new IPAddress(
            [
                10,
                (byte)(addressIndex >> 16),
                (byte)(addressIndex >> 8),
                (byte)addressIndex
            ]);
            return true;
        }

        ulong remainder = addressIndex - TenNetworkSize;
        if (remainder < SeventeenTwoNetworkSize)
        {
            address = new IPAddress(
            [
                172,
                (byte)(16 + (remainder >> 16)),
                (byte)(remainder >> 8),
                (byte)remainder
            ]);
            return true;
        }

        remainder -= SeventeenTwoNetworkSize;
        address = new IPAddress(
        [
            192,
            168,
            (byte)(remainder >> 8),
            (byte)remainder
        ]);
        return true;
    }

    public static bool IsValidGf32PrimitiveElement(byte element)
    {
        if (element is 0 or > 31)
        {
            return false;
        }

        Span<bool> visited = stackalloc bool[32];
        byte value = 1;
        for (int count = 0; count < 31; count++)
        {
            if (visited[value])
            {
                return false;
            }

            visited[value] = true;
            value = Gf32Multiply(value, element);
        }

        return value == 1 && visited[1..].IndexOf(false) < 0;
    }

    private static string Encode(ulong addressIndex, int pin)
    {
        if (addressIndex >= PrivateAddressCount || pin is < 0 or >= PinLimit)
        {
            throw new ArgumentOutOfRangeException();
        }

        ulong payload = (addressIndex * PinLimit) + (uint)pin;
        Span<byte> symbols = stackalloc byte[CodeLength];
        for (int index = 0; index < DataSymbolCount; index++)
        {
            int shift = (DataSymbolCount - 1 - index) * 5;
            symbols[index] = (byte)((payload >> shift) & 0x1F);
        }

        symbols[DataSymbolCount] = ComputeCheckSymbol(symbols[..DataSymbolCount]);
        Span<char> chars = stackalloc char[CodeLength];
        for (int index = 0; index < CodeLength; index++)
        {
            chars[index] = Alphabet[symbols[index]];
        }

        return new string(chars);
    }

    private static byte ComputeCheckSymbol(ReadOnlySpan<byte> dataSymbols)
    {
        byte check = 0;
        for (int index = 0; index < DataSymbolCount; index++)
        {
            check ^= Gf32Multiply(dataSymbols[index], CheckWeights[index]);
        }

        return check;
    }

    private static byte Gf32Multiply(byte left, byte right)
    {
        byte result = 0;
        for (int bit = 0; bit < 5 && right != 0; bit++)
        {
            if ((right & 1) != 0)
            {
                result ^= left;
            }

            bool highBit = (left & 0x10) != 0;
            left = (byte)((left << 1) & 0x1F);
            if (highBit)
            {
                left ^= Gf32ReductionPolynomial;
            }

            right >>= 1;
        }

        return result;
    }

    private static byte[] BuildPrimitivePowerCycle()
    {
        byte[] cycle = new byte[31];
        byte value = 1;
        for (int index = 0; index < cycle.Length; index++)
        {
            cycle[index] = value;
            value = Gf32Multiply(value, Gf32PrimitiveElement);
        }

        return cycle;
    }

    private static IPAddress NormalizeAddress(IPAddress address)
    {
        return address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetworkV6
            && address.IsIPv4MappedToIPv6
            ? address.MapToIPv4()
            : address;
    }
}

public enum ConnectionCodeFailureReason
{
    None,
    Empty,
    InvalidLength,
    InvalidCharacter,
    ChecksumMismatch,
    InvalidPayload,
    UnsupportedAddress
}

public sealed class ConnectionCodeInfo
{
    internal static ConnectionCodeInfo Empty { get; } =
        new(string.Empty, IPAddress.None, 0, string.Empty);

    public ConnectionCodeInfo(
        string code,
        IPAddress address,
        int pin,
        string authenticationPassword)
    {
        Code = code;
        Address = address;
        Pin = pin;
        AuthenticationPassword = authenticationPassword;
    }

    public string Code { get; }

    public IPAddress Address { get; }

    public int Pin { get; }

    public string AuthenticationPassword { get; }
}
