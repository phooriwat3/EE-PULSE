using System.Net;

namespace EePulse.Agent.Core.Networking;

public sealed class AllowedNetworkPolicy
{
    private static readonly Ipv4Network Unspecified = ParseKnown("0.0.0.0/8");
    private static readonly Ipv4Network Loopback = ParseKnown("127.0.0.0/8");
    private static readonly Ipv4Network LinkLocal = ParseKnown("169.254.0.0/16");
    private static readonly Ipv4Network Multicast = ParseKnown("224.0.0.0/4");
    private readonly Ipv4Network[] networks;

    private AllowedNetworkPolicy(Ipv4Network[] networks) => this.networks = networks;

    public IReadOnlyList<string> Networks => networks.Select(static network => network.ToString()).ToArray();

    public bool Contains(IPAddress address)
    {
        if (!IsUnicastTarget(address) || !Ipv4Network.TryToUInt32(address, out var value))
        {
            return false;
        }

        return networks.Any(network =>
            network.Contains(value) &&
            (network.PrefixLength >= 31 ||
             (value != network.NetworkAddress && value != network.BroadcastAddress)));
    }

    public bool Contains(string address) => IPAddress.TryParse(address, out var parsed) && Contains(parsed);

    public bool Contains(Ipv4Network network) => networks.Any(allowed => allowed.Contains(network));

    public static bool TryCreate(
        IEnumerable<string> values,
        bool allowDevelopmentNetworks,
        out AllowedNetworkPolicy? policy)
    {
        ArgumentNullException.ThrowIfNull(values);

        policy = null;
        var parsed = new List<Ipv4Network>();
        foreach (var value in values)
        {
            if (!Ipv4Network.TryParse(value, out var network) || IsProhibited(network, allowDevelopmentNetworks))
            {
                return false;
            }

            if (parsed.Any(existing => existing.Contains(network) || network.Contains(existing)))
            {
                return false;
            }

            parsed.Add(network);
            if (parsed.Count > 64)
            {
                return false;
            }
        }

        if (parsed.Count == 0)
        {
            return false;
        }

        parsed.Sort(static (left, right) =>
        {
            var addressComparison = left.NetworkAddress.CompareTo(right.NetworkAddress);
            return addressComparison != 0 ? addressComparison : left.PrefixLength.CompareTo(right.PrefixLength);
        });
        policy = new AllowedNetworkPolicy([.. parsed]);
        return true;
    }

    public static bool IsUnicastTarget(IPAddress address)
    {
        if (!Ipv4Network.TryToUInt32(address, out var value))
        {
            return false;
        }

        return value != uint.MaxValue &&
               !Unspecified.Contains(value) &&
               !Multicast.Contains(value);
    }

    private static bool IsProhibited(Ipv4Network network, bool allowDevelopmentNetworks) =>
        network.PrefixLength < 8 ||
        Unspecified.Contains(network) ||
        Multicast.Contains(network) ||
        network.Contains(Unspecified) ||
        network.Contains(Multicast) ||
        (!allowDevelopmentNetworks &&
         (Loopback.Contains(network) || LinkLocal.Contains(network) ||
          network.Contains(Loopback) || network.Contains(LinkLocal))) ||
        network.Contains(uint.MaxValue);

    private static Ipv4Network ParseKnown(string value)
    {
        _ = Ipv4Network.TryParse(value, out var network);
        return network;
    }
}
