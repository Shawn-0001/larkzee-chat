using System.Globalization;
using System.Net;

namespace LarkzeeChat.Forms;

internal static class Ipv4InputValidation
{
    /// <summary>
    /// Accept only the familiar four-octet decimal form. IPAddress.TryParse
    /// alone also accepts legacy shorthand forms such as "127.1".
    /// </summary>
    internal static bool TryParseDottedDecimal(string? input, out IPAddress address)
    {
        address = IPAddress.None;
        if (string.IsNullOrWhiteSpace(input))
        {
            return false;
        }

        string[] octets = input.Trim().Split('.', StringSplitOptions.None);
        if (octets.Length != 4)
        {
            return false;
        }

        byte[] bytes = new byte[4];
        for (int index = 0; index < octets.Length; index++)
        {
            string octet = octets[index];
            if (octet.Length is 0 or > 3 || octet.Any(character => character is < '0' or > '9'))
            {
                return false;
            }

            if (!byte.TryParse(octet, NumberStyles.None, CultureInfo.InvariantCulture, out bytes[index]))
            {
                return false;
            }
        }

        address = new IPAddress(bytes);
        return true;
    }
}
