using System.Net;
using System.Net.Sockets;
using EePulse.Domain.Common;

namespace EePulse.Application.Agents;

public readonly record struct Ipv4Network(uint Network, int Prefix)
{
    public uint Mask => Prefix == 0 ? 0 : uint.MaxValue << (32 - Prefix);
    public uint End => Network | ~Mask;
    public bool Contains(Ipv4Network other) => Network <= other.Network && End >= other.End;
    public bool Contains(IPAddress address) { var value = ToUInt32(address); return value >= Network && value <= End; }
    public override string ToString() => $"{new IPAddress(BitConverter.GetBytes(Network).Reverse().ToArray())}/{Prefix}";
    internal static uint ToUInt32(IPAddress address)
    {
        if (address.AddressFamily != AddressFamily.InterNetwork) throw new DomainValidationException("network", "Only IPv4 is supported.");
        var b = address.GetAddressBytes(); return ((uint)b[0] << 24) | ((uint)b[1] << 16) | ((uint)b[2] << 8) | b[3];
    }
}

public static class Ipv4NetworkPolicy
{
    public static IReadOnlyList<string> Normalize(IEnumerable<string> values, bool development)
    {
        var parsed = values.Select(Parse).OrderBy(x => x.Network).ThenBy(x => x.Prefix).ToArray();
        if (parsed.Length is < 1 or > 64) throw new DomainValidationException(nameof(values), "Allowed networks must contain between 1 and 64 entries.");
        for (var i = 0; i < parsed.Length; i++)
        {
            Validate(parsed[i], development);
            for (var j = 0; j < i; j++) if (parsed[j].Contains(parsed[i])) throw new DomainValidationException(nameof(values), "Allowed networks cannot contain duplicates or redundant overlaps.");
        }
        return parsed.Select(x => x.ToString()).ToArray();
    }
    public static bool IsNarrowerOrEqual(IEnumerable<string> candidate, IEnumerable<string> ceiling)
    {
        var caps = ceiling.Select(Parse).ToArray(); return candidate.Select(Parse).All(x => caps.Any(c => c.Contains(x)));
    }
    public static bool ContainsAddress(IEnumerable<string> networks, string address)
    {
        if (!IPAddress.TryParse(address, out var ip) || ip.AddressFamily != AddressFamily.InterNetwork) return false;
        var value = Ipv4Network.ToUInt32(ip);
        return networks.Select(Parse).Any(network => network.Contains(ip) &&
            (network.Prefix >= 31 || value != network.Network && value != network.End));
    }
    public static Ipv4Network Parse(string value)
    {
        var parts = value.Trim().Split('/', 2); if (!IPAddress.TryParse(parts[0], out var ip) || ip.AddressFamily != AddressFamily.InterNetwork)
            throw new DomainValidationException(nameof(value), "Network must be an IPv4 address or CIDR.");
        var prefix = parts.Length == 1 ? 32 : int.TryParse(parts[1], out var p) ? p : -1;
        if (prefix is < 8 or > 32) throw new DomainValidationException(nameof(value), "CIDR prefix must be between /8 and /32.");
        var mask = uint.MaxValue << (32 - prefix); return new Ipv4Network(Ipv4Network.ToUInt32(ip) & mask, prefix);
    }
    private static void Validate(Ipv4Network n, bool development)
    {
        if (n.Network < 0x01000000 || (n.Network >= 0xE0000000) || n.Contains(IPAddress.Parse("255.255.255.255")))
            throw new DomainValidationException("network", "Unspecified, multicast, broadcast, and unrestricted networks are prohibited.");
        if (!development && (n.Contains(IPAddress.Parse("127.0.0.1")) || n.Contains(IPAddress.Parse("169.254.0.1"))))
            throw new DomainValidationException("network", "Loopback and link-local networks are Development-only.");
    }
}
