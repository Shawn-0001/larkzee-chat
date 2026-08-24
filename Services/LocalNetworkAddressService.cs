using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;

namespace LarkzeeChat.Services;

/// <summary>
/// Finds private IPv4 addresses that can be shown in the connection settings.
/// The result is a deterministic, user-selectable list because a computer can
/// legitimately have more than one active LAN/VPN adapter.
/// </summary>
public static class LocalNetworkAddressService
{
    public static IReadOnlyList<LocalNetworkAddressCandidate> GetCandidates()
    {
        var candidatesByAddress = new Dictionary<string, LocalNetworkAddressCandidate>(StringComparer.Ordinal);

        NetworkInterface[] interfaces;
        try
        {
            interfaces = NetworkInterface.GetAllNetworkInterfaces();
        }
        catch (NetworkInformationException)
        {
            return [];
        }
        catch (InvalidOperationException)
        {
            return [];
        }

        foreach (NetworkInterface networkInterface in interfaces)
        {
            if (networkInterface.OperationalStatus != OperationalStatus.Up
                || networkInterface.NetworkInterfaceType is NetworkInterfaceType.Loopback
                    or NetworkInterfaceType.Tunnel)
            {
                continue;
            }

            IPInterfaceProperties properties;
            try
            {
                properties = networkInterface.GetIPProperties();
            }
            catch (NetworkInformationException)
            {
                continue;
            }
            catch (InvalidOperationException)
            {
                continue;
            }

            bool hasIpv4Gateway = properties.GatewayAddresses.Any(gateway =>
                gateway.Address.AddressFamily == AddressFamily.InterNetwork
                && !IPAddress.Any.Equals(gateway.Address));
            string interfaceName = string.IsNullOrWhiteSpace(networkInterface.Name)
                ? networkInterface.Description
                : networkInterface.Name;

            foreach (UnicastIPAddressInformation unicast in properties.UnicastAddresses)
            {
                IPAddress address = unicast.Address;
                if (address.AddressFamily != AddressFamily.InterNetwork
                    || !ConnectionCodeService.TryGetPrivateIpIndex(address, out _))
                {
                    continue;
                }

                var candidate = new LocalNetworkAddressCandidate(
                    address,
                    interfaceName,
                    networkInterface.Description,
                    networkInterface.NetworkInterfaceType,
                    hasIpv4Gateway);

                string addressKey = address.ToString();
                if (!candidatesByAddress.TryGetValue(addressKey, out LocalNetworkAddressCandidate? current)
                    || ComparePriority(candidate, current) < 0)
                {
                    candidatesByAddress[addressKey] = candidate;
                }
            }
        }

        return candidatesByAddress.Values
            .OrderBy(candidate => candidate, CandidateComparer.Instance)
            .ToArray();
    }

    public static bool TryGetPreferredAddress(out LocalNetworkAddressCandidate candidate)
    {
        candidate = null!;
        IReadOnlyList<LocalNetworkAddressCandidate> candidates = GetCandidates();
        if (candidates.Count == 0)
        {
            return false;
        }

        candidate = candidates[0];
        return true;
    }

    private static int ComparePriority(
        LocalNetworkAddressCandidate left,
        LocalNetworkAddressCandidate right)
    {
        int comparison = CandidateComparer.Instance.Compare(left, right);
        return comparison;
    }

    private sealed class CandidateComparer : IComparer<LocalNetworkAddressCandidate>
    {
        internal static CandidateComparer Instance { get; } = new();

        public int Compare(
            LocalNetworkAddressCandidate? left,
            LocalNetworkAddressCandidate? right)
        {
            if (ReferenceEquals(left, right))
            {
                return 0;
            }

            if (left is null)
            {
                return 1;
            }

            if (right is null)
            {
                return -1;
            }

            int gatewayComparison = right.HasIpv4Gateway.CompareTo(left.HasIpv4Gateway);
            if (gatewayComparison != 0)
            {
                return gatewayComparison;
            }

            int typeComparison = InterfaceTypeRank(left.InterfaceType)
                .CompareTo(InterfaceTypeRank(right.InterfaceType));
            if (typeComparison != 0)
            {
                return typeComparison;
            }

            int nameComparison = StringComparer.OrdinalIgnoreCase.Compare(
                left.InterfaceName,
                right.InterfaceName);
            return nameComparison != 0
                ? nameComparison
                : CompareIpv4(left.Address, right.Address);
        }

        private static int InterfaceTypeRank(NetworkInterfaceType type)
        {
            return type switch
            {
                NetworkInterfaceType.Ethernet => 0,
                NetworkInterfaceType.Wireless80211 => 1,
                _ => 2
            };
        }

        private static int CompareIpv4(IPAddress left, IPAddress right)
        {
            byte[] leftBytes = left.GetAddressBytes();
            byte[] rightBytes = right.GetAddressBytes();
            for (int index = 0; index < leftBytes.Length; index++)
            {
                int comparison = leftBytes[index].CompareTo(rightBytes[index]);
                if (comparison != 0)
                {
                    return comparison;
                }
            }

            return 0;
        }
    }
}

public sealed class LocalNetworkAddressCandidate
{
    public LocalNetworkAddressCandidate(
        IPAddress address,
        string interfaceName,
        string interfaceDescription,
        NetworkInterfaceType interfaceType,
        bool hasIpv4Gateway)
    {
        Address = address;
        InterfaceName = interfaceName;
        InterfaceDescription = interfaceDescription;
        InterfaceType = interfaceType;
        HasIpv4Gateway = hasIpv4Gateway;
    }

    public IPAddress Address { get; }

    public string InterfaceName { get; }

    public string InterfaceDescription { get; }

    public NetworkInterfaceType InterfaceType { get; }

    public bool HasIpv4Gateway { get; }

    public string DisplayText => string.IsNullOrWhiteSpace(InterfaceName)
        ? Address.ToString()
        : $"{Address}（{InterfaceName}）";

    public override string ToString() => DisplayText;
}
