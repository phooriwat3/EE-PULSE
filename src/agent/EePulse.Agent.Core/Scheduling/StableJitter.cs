using System.Buffers.Binary;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace EePulse.Agent.Core.Scheduling;

public static class StableJitter
{
    public static TimeSpan ForProbe(Guid installationId, Guid probeId, long configurationVersion, TimeSpan interval)
    {
        ArgumentOutOfRangeException.ThrowIfEqual(installationId, Guid.Empty);
        ArgumentOutOfRangeException.ThrowIfEqual(probeId, Guid.Empty);
        ArgumentOutOfRangeException.ThrowIfLessThan(configurationVersion, 1);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(interval, TimeSpan.Zero);

        var key = string.Create(
            CultureInfo.InvariantCulture,
            $"wp04-jitter-v1:{installationId:N}:{probeId:N}:{configurationVersion}");
        Span<byte> hash = stackalloc byte[SHA256.HashSizeInBytes];
        SHA256.HashData(Encoding.UTF8.GetBytes(key), hash);

        var bucket = BinaryPrimitives.ReadUInt64LittleEndian(hash);
        return TimeSpan.FromTicks((long)(bucket % (ulong)interval.Ticks));
    }

    public static TimeSpan ForProbe(Guid probeId, TimeSpan interval)
    {
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(interval, TimeSpan.Zero);

        Span<byte> probeBytes = stackalloc byte[16];
        probeId.TryWriteBytes(probeBytes);

        Span<byte> hash = stackalloc byte[SHA256.HashSizeInBytes];
        SHA256.HashData(probeBytes, hash);

        var bucket = BinaryPrimitives.ReadUInt64LittleEndian(hash);
        var offsetTicks = bucket % (ulong)interval.Ticks;
        return TimeSpan.FromTicks((long)offsetTicks);
    }
}
