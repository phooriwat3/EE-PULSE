using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using EePulse.Agent.Core.Security;
using EePulse.Contracts.Agents;

namespace EePulse.Agent.Infrastructure.Storage;

internal sealed class AtomicProtectedJsonFile<T>(
    string path,
    ISecretProtector protector,
    IProtectedFileAccessPolicy accessPolicy)
    : IDisposable
    where T : class
{
    private static readonly JsonSerializerOptions JsonOptions = CreateJsonOptions();

    private static JsonSerializerOptions CreateJsonOptions()
    {
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web)
        {
            UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        };
        AgentJsonContract.AddConverters(options);
        return options;
    }

    private readonly SemaphoreSlim fileLock = new(1, 1);

    public async ValueTask<T?> LoadAsync(CancellationToken cancellationToken)
    {
        await fileLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (!File.Exists(path))
            {
                return null;
            }

            var protectedBytes = await File.ReadAllBytesAsync(path, cancellationToken).ConfigureAwait(false);
            byte[]? plaintext = null;
            try
            {
                plaintext = protector.Unprotect(protectedBytes);
                return JsonSerializer.Deserialize<T>(plaintext, JsonOptions) ?? throw new ProtectedStoreException();
            }
            catch (Exception exception) when (exception is CryptographicException or JsonException)
            {
                throw new ProtectedStoreException(exception);
            }
            finally
            {
                CryptographicOperations.ZeroMemory(protectedBytes);
                if (plaintext is not null)
                {
                    CryptographicOperations.ZeroMemory(plaintext);
                }
            }
        }
        finally
        {
            fileLock.Release();
        }
    }

    public async ValueTask SaveAsync(T value, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(value);
        await fileLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var directory = Path.GetDirectoryName(path) ?? throw new ProtectedStoreException();
            accessPolicy.SecureDirectory(directory);
            var plaintext = JsonSerializer.SerializeToUtf8Bytes(value, JsonOptions);
            byte[]? protectedBytes = null;
            var temporaryPath = Path.Combine(directory, $".{Path.GetFileName(path)}.{Guid.NewGuid():N}.tmp");
            try
            {
                protectedBytes = protector.Protect(plaintext);
                await using (var stream = new FileStream(
                    temporaryPath,
                    FileMode.CreateNew,
                    FileAccess.Write,
                    FileShare.None,
                    4096,
                    FileOptions.Asynchronous | FileOptions.WriteThrough))
                {
                    await stream.WriteAsync(protectedBytes, cancellationToken).ConfigureAwait(false);
                    await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
                    stream.Flush(flushToDisk: true);
                }

                accessPolicy.SecureFile(temporaryPath);
                File.Move(temporaryPath, path, overwrite: true);
                accessPolicy.SecureFile(path);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or CryptographicException)
            {
                throw new ProtectedStoreException(exception);
            }
            finally
            {
                CryptographicOperations.ZeroMemory(plaintext);
                if (protectedBytes is not null)
                {
                    CryptographicOperations.ZeroMemory(protectedBytes);
                }

                if (File.Exists(temporaryPath))
                {
                    File.Delete(temporaryPath);
                }
            }
        }
        finally
        {
            fileLock.Release();
        }
    }

    public async ValueTask DeleteAsync(CancellationToken cancellationToken)
    {
        await fileLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        finally
        {
            fileLock.Release();
        }
    }

    public void Dispose() => fileLock.Dispose();
}
