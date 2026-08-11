using System.Security.Cryptography;
using EePulse.Agent.Core.Security;
using EePulse.Agent.Core.Transport;
using EePulse.Agent.Infrastructure.Storage;

namespace EePulse.Agent.Tests;

public sealed class ProtectedStorageTests : IDisposable
{
    private readonly string directory = Path.Combine(Path.GetTempPath(), $"ee-pulse-agent-{Guid.NewGuid():N}");

    [Fact]
    public async Task IdentityRoundTripsWithoutPlaintextPersistence()
    {
        var identity = ConfigurationApplicationTests.Identity();
        var options = new AgentStorageOptions(directory, "synthetic-test-identity", false);
        using var store = new ProtectedAgentIdentityStore(options, new XorProtector(), new TestAccessPolicy());

        await store.SaveAsync(identity, TestContext.Current.CancellationToken);
        var bytes = await File.ReadAllBytesAsync(Path.Combine(directory, "identity.dat"), TestContext.Current.CancellationToken);
        var text = System.Text.Encoding.UTF8.GetString(bytes);

        Assert.DoesNotContain(identity.ActiveCredential.Secret, text, StringComparison.Ordinal);
        var loaded = await store.LoadAsync(TestContext.Current.CancellationToken);
        Assert.NotNull(loaded);
        Assert.Equal(identity.AgentId, loaded.AgentId);
        Assert.Equal(identity.LocalAllowedNetworks, loaded.LocalAllowedNetworks);
        Assert.Equal(identity.ActiveCredential.Secret, loaded.ActiveCredential.Secret);
    }

    [Fact]
    public async Task CorruptProtectedStateFailsClosedWithSanitizedError()
    {
        var options = new AgentStorageOptions(directory, "synthetic-test-identity", false);
        using var store = new ProtectedAgentIdentityStore(options, new XorProtector(), new TestAccessPolicy());
        Directory.CreateDirectory(directory);
        await File.WriteAllBytesAsync(Path.Combine(directory, "identity.dat"), [1, 2, 3], TestContext.Current.CancellationToken);

        var exception = await Assert.ThrowsAsync<ProtectedStoreException>(async () =>
            await store.LoadAsync(TestContext.Current.CancellationToken));

        Assert.Equal("The protected Agent state could not be read or written.", exception.Message);
    }

    [Fact]
    public async Task CorruptProtectedConfigurationFailsClosed()
    {
        var options = new AgentStorageOptions(directory, "synthetic-test-identity", false);
        using var store = new ProtectedAgentConfigurationStore(options, new XorProtector(), new TestAccessPolicy());
        Directory.CreateDirectory(directory);
        await File.WriteAllBytesAsync(Path.Combine(directory, "configuration.dat"), [1, 2, 3], TestContext.Current.CancellationToken);

        await Assert.ThrowsAsync<ProtectedStoreException>(async () =>
            await store.LoadAsync(TestContext.Current.CancellationToken));
    }

    [Fact]
    public void ProductionStorageFailsClosedOutsideWindowsProgramDataBoundary()
    {
        var options = new AgentStorageOptions(directory, "synthetic-test-identity", true);

        Assert.Throws<InvalidOperationException>(options.Validate);
        Assert.Throws<InvalidOperationException>(() =>
            new AgentClientOptions(new Uri("http://127.0.0.1/"), true).Validate());
    }

    public void Dispose()
    {
        if (Directory.Exists(directory))
        {
            Directory.Delete(directory, recursive: true);
        }

        GC.SuppressFinalize(this);
    }

    private sealed class XorProtector : ISecretProtector
    {
        private static readonly byte[] Key = RandomNumberGenerator.GetBytes(32);

        public byte[] Protect(ReadOnlySpan<byte> plaintext) => Transform(plaintext);

        public byte[] Unprotect(ReadOnlySpan<byte> protectedData) => Transform(protectedData);

        private static byte[] Transform(ReadOnlySpan<byte> input)
        {
            var result = input.ToArray();
            for (var index = 0; index < result.Length; index++)
            {
                result[index] ^= Key[index % Key.Length];
            }

            return result;
        }
    }

    private sealed class TestAccessPolicy : IProtectedFileAccessPolicy
    {
        public void SecureDirectory(string path) => Directory.CreateDirectory(path);

        public void SecureFile(string path)
        {
        }
    }
}
