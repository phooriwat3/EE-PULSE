using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Text;
using EePulse.Agent.Core.Security;

namespace EePulse.Agent.Infrastructure.Security;

public sealed class DpapiLocalMachineProtector : ISecretProtector
{
    private static readonly byte[] Entropy = Encoding.UTF8.GetBytes("EE-Pulse-Agent-v1/local-machine");

    public byte[] Protect(ReadOnlySpan<byte> plaintext)
    {
        if (OperatingSystem.IsWindows())
        {
            return ProtectWindows(plaintext);
        }

        throw new PlatformNotSupportedException("Windows DPAPI LocalMachine protection is required.");
    }

    public byte[] Unprotect(ReadOnlySpan<byte> protectedData)
    {
        if (OperatingSystem.IsWindows())
        {
            return UnprotectWindows(protectedData);
        }

        throw new PlatformNotSupportedException("Windows DPAPI LocalMachine protection is required.");
    }

    [SupportedOSPlatform("windows")]
    private static byte[] ProtectWindows(ReadOnlySpan<byte> plaintext)
    {
        var copy = plaintext.ToArray();
        try
        {
            return ProtectedData.Protect(copy, Entropy, DataProtectionScope.LocalMachine);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(copy);
        }
    }

    [SupportedOSPlatform("windows")]
    private static byte[] UnprotectWindows(ReadOnlySpan<byte> protectedData) =>
        ProtectedData.Unprotect(protectedData.ToArray(), Entropy, DataProtectionScope.LocalMachine);

}
