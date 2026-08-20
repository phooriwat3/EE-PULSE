using System.Net;

namespace EePulse.Agent.Core.Probing;

/// <summary>Normalizes the WP-04 IPv4-literal-only target form without DNS lookup.</summary>
public static class Ipv4ProbeTarget
{
    public static bool TryNormalize(string? value, out string? normalizedTarget)
    {
        normalizedTarget = null;
        if (string.IsNullOrWhiteSpace(value) ||
            !IPAddress.TryParse(value, out var address) ||
            address.AddressFamily != System.Net.Sockets.AddressFamily.InterNetwork)
        {
            return false;
        }

        normalizedTarget = address.ToString();
        return true;
    }
}
