using System.Net;
using System.Net.Sockets;

namespace EePulse.Agent.Core.Networking;

public readonly record struct Ipv4Network(uint NetworkAddress, int PrefixLength)
{
    public uint Mask => PrefixLength == 0 ? 0 : uint.MaxValue << (32 - PrefixLength);

    public uint BroadcastAddress => NetworkAddress | ~Mask;

    public bool Contains(uint address) => (address & Mask) == NetworkAddress;

    public bool Contains(Ipv4Network other) =>
        PrefixLength <= other.PrefixLength && Contains(other.NetworkAddress);

    public bool Contains(IPAddress address) => TryToUInt32(address, out var value) && Contains(value);

    public override string ToString() => $"{FromUInt32(NetworkAddress)}/{PrefixLength}";

    public static bool TryParse(string? value, out Ipv4Network network)
    {
        network = default;
        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        var parts = value.Trim().Split('/', StringSplitOptions.TrimEntries);
        if (parts.Length is < 1 or > 2 ||
            !IPAddress.TryParse(parts[0], out var address) ||
            !TryToUInt32(address, out var addressValue))
        {
            return false;
        }

        var prefixLength = 32;
        if (parts.Length == 2 &&
            (!int.TryParse(parts[1], System.Globalization.NumberStyles.None,
                System.Globalization.CultureInfo.InvariantCulture, out prefixLength) ||
             prefixLength is < 0 or > 32))
        {
            return false;
        }

        var mask = uint.MaxValue << (32 - prefixLength);
        network = new Ipv4Network(addressValue & mask, prefixLength);
        return true;
    }

    public static bool TryToUInt32(IPAddress address, out uint value)
    {
        value = 0;
        if (address.AddressFamily != AddressFamily.InterNetwork)
        {
            return false;
        }

        Span<byte> bytes = stackalloc byte[4];
        if (!address.TryWriteBytes(bytes, out var written) || written != bytes.Length)
        {
            return false;
        }

        value = ((uint)bytes[0] << 24) |
                ((uint)bytes[1] << 16) |
                ((uint)bytes[2] << 8) |
                bytes[3];
        return true;
    }

    private static IPAddress FromUInt32(uint value) => new(
    [
        (byte)(value >> 24),
        (byte)(value >> 16),
        (byte)(value >> 8),
        (byte)value,
    ]);
}
