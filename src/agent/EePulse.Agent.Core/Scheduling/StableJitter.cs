using System.Buffers.Binary;
using System.Security.Cryptography;

namespace EePulse.Agent.Core.Scheduling;

public static class StableJitter
{
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
