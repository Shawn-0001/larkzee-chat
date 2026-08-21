using System;
using System.Security.Cryptography;
using System.Text;

namespace LarkzeeChat.Networking;

internal static class AuthenticationService
{
    internal const int MinimumManualPasswordLength = 8;
    internal const int MaximumManualPasswordLength = 64;
    internal const int MaximumManualPasswordUtf8Bytes = 256;

    internal static bool TryValidateManualPassword(
        string? password,
        out string validatedPassword)
    {
        validatedPassword = string.Empty;
        if (string.IsNullOrWhiteSpace(password)
            || password.Trim() != password
            || password.Length < MinimumManualPasswordLength
            || password.Length > MaximumManualPasswordLength
            || Encoding.UTF8.GetByteCount(password) > MaximumManualPasswordUtf8Bytes)
        {
            return false;
        }

        validatedPassword = password;
        return true;
    }

    internal static byte[] CreateChallenge()
    {
        return RandomNumberGenerator.GetBytes(32);
    }

    internal static bool TryDecodeBase64(
        string? value,
        int expectedLength,
        out byte[] decoded)
    {
        decoded = Array.Empty<byte>();
        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        try
        {
            byte[] candidate = Convert.FromBase64String(value);
            if (candidate.Length != expectedLength)
            {
                return false;
            }

            decoded = candidate;
            return true;
        }
        catch (FormatException)
        {
            return false;
        }
    }
}
