using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using EePulse.Agent.Core.Configuration;
using EePulse.Agent.Core.Identity;
using EePulse.Agent.Core.Networking;
using EePulse.Agent.Core.Runtime;
using EePulse.Contracts.Agents;

namespace EePulse.Agent.Core.Transport;

public sealed class AgentApiClient
{
    private const int MaximumConfigurationBytes = 2 * 1024 * 1024;
    private static readonly JsonSerializerOptions JsonOptions = CreateJsonOptions();
    private static readonly JsonSerializerOptions ProblemJsonOptions = new(JsonSerializerDefaults.Web);

    private static JsonSerializerOptions CreateJsonOptions()
    {
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web)
        {
            UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        };
        AgentJsonContract.AddConverters(options);
        return options;
    }

    private readonly HttpClient httpClient;
    private readonly IAgentIdentityStore identityStore;
    private readonly IAgentRevocationHandler revocationHandler;
    private readonly IAgentRetryDelay retryDelay;
    private readonly bool allowDevelopmentNetworks;

    public AgentApiClient(
        HttpClient httpClient,
        IAgentIdentityStore identityStore,
        IAgentRevocationHandler revocationHandler,
        IAgentRetryDelay retryDelay,
        AgentClientOptions options)
    {
        this.httpClient = httpClient;
        this.identityStore = identityStore;
        this.revocationHandler = revocationHandler;
        this.retryDelay = retryDelay;
        allowDevelopmentNetworks = !options.IsProduction;
        options.Validate();
        httpClient.BaseAddress = options.ServerBaseAddress;
    }

    public async ValueTask<AgentIdentity> EnrollAsync(
        string enrollmentToken,
        Guid clientInstanceId,
        string machineName,
        string agentVersion,
        IReadOnlyList<string> localAllowedNetworks,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(enrollmentToken);
        if (!AllowedNetworkPolicy.TryCreate(localAllowedNetworks, allowDevelopmentNetworks, out var localPolicy))
        {
            throw new InvalidOperationException("Local AllowedNetworks configuration is invalid.");
        }

        var request = new AgentEnrollmentRequest(
            AgentContract.SchemaVersion,
            enrollmentToken,
            clientInstanceId,
            machineName.Trim(),
            agentVersion,
            localPolicy!.Networks,
            DateTimeOffset.UtcNow);

        using var response = await httpClient.PostAsJsonAsync(
            "api/v1/agents/enroll",
            request,
            JsonOptions,
            cancellationToken).ConfigureAwait(false);
        var payload = await ReadSuccessAsync<AgentEnrollmentResponse>(response, cancellationToken).ConfigureAwait(false);
        EnsureSchema(payload.SchemaVersion);
        if (!Uri.TryCreate(payload.ConfigurationUrl, UriKind.Relative, out _) ||
            !string.Equals(
                payload.ConfigurationUrl,
                $"/api/v1/agents/{payload.AgentId:D}/configuration",
                StringComparison.Ordinal))
        {
            throw new AgentApiException(HttpStatusCode.BadRequest, AgentProblemCodes.RequestInvalid);
        }

        var identity = new AgentIdentity(
            payload.AgentId,
            payload.AgentGroupId,
            clientInstanceId,
            machineName.Trim(),
            agentVersion,
            localPolicy.Networks,
            new AgentCredential(payload.CredentialId, payload.AgentCredential, payload.CredentialExpiresAt, payload.RotateAfter),
            null,
            payload.HeartbeatIntervalSeconds,
            payload.HeartbeatExpiresAfterSeconds,
            payload.DesiredConfigurationVersion);
        await identityStore.SaveAsync(identity, cancellationToken).ConfigureAwait(false);
        return identity;
    }

    public async ValueTask<AgentHeartbeatResponse> SendHeartbeatAsync(
        AgentIdentity identity,
        AgentHeartbeatRequest heartbeat,
        CancellationToken cancellationToken)
    {
        for (var attempt = 0; ; attempt++)
        {
            HttpResponseMessage response;
            try
            {
                using var request = CreateAuthenticatedRequest(
                    HttpMethod.Post,
                    $"api/v1/agents/{identity.AgentId:D}/heartbeat",
                    identity.AuthenticationCredential.Secret,
                    heartbeat);
                response = await httpClient.SendAsync(request, cancellationToken).ConfigureAwait(false);
            }
            catch (HttpRequestException) when (attempt < 2)
            {
                await retryDelay.DelayAsync(TimeSpan.FromSeconds(1 << attempt), cancellationToken).ConfigureAwait(false);
                continue;
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested && attempt < 2)
            {
                await retryDelay.DelayAsync(TimeSpan.FromSeconds(1 << attempt), cancellationToken).ConfigureAwait(false);
                continue;
            }

            using (response)
            {
                if (response.IsSuccessStatusCode)
                {
                    var payload = await ReadSuccessAsync<AgentHeartbeatResponse>(response, cancellationToken).ConfigureAwait(false);
                    await PromotePendingAfterSuccessfulUseAsync(identity, cancellationToken).ConfigureAwait(false);
                    EnsureSchema(payload.SchemaVersion);
                    return payload;
                }

                if (await HandleRevocationAsync(response, cancellationToken).ConfigureAwait(false))
                {
                    throw new AgentApiException(HttpStatusCode.Gone, AgentProblemCodes.AgentRevoked);
                }

                if (response.StatusCode == HttpStatusCode.Unauthorized && identity.PendingCredential is not null)
                {
                    identity = await DiscardPendingCredentialAsync(identity, cancellationToken).ConfigureAwait(false);
                    continue;
                }

                if (attempt >= 2 || !IsRetryable(response.StatusCode))
                {
                    throw await CreateExceptionAsync(response, cancellationToken).ConfigureAwait(false);
                }

                await retryDelay.DelayAsync(GetRetryDelay(response, attempt), cancellationToken).ConfigureAwait(false);
            }
        }
    }

    public async ValueTask<(AgentConfigurationResponse Configuration, string StrongETag)?> PullConfigurationAsync(
        AgentIdentity identity,
        string? currentStrongETag,
        CancellationToken cancellationToken)
    {
        try
        {
            return await PullConfigurationCoreAsync(identity, currentStrongETag, cancellationToken).ConfigureAwait(false);
        }
        catch (AgentApiException exception) when (
            exception.StatusCode == HttpStatusCode.Unauthorized && identity.PendingCredential is not null)
        {
            identity = await DiscardPendingCredentialAsync(identity, cancellationToken).ConfigureAwait(false);
            return await PullConfigurationCoreAsync(identity, currentStrongETag, cancellationToken).ConfigureAwait(false);
        }
    }

    private async ValueTask<(AgentConfigurationResponse Configuration, string StrongETag)?> PullConfigurationCoreAsync(
        AgentIdentity identity,
        string? currentStrongETag,
        CancellationToken cancellationToken)
    {
        using var request = CreateAuthenticatedRequest(
            HttpMethod.Get,
            $"api/v1/agents/{identity.AgentId:D}/configuration",
            identity.AuthenticationCredential.Secret);
        if (currentStrongETag is not null)
        {
            EnsureStrongETag(currentStrongETag);
            request.Headers.IfNoneMatch.Add(EntityTagHeaderValue.Parse(currentStrongETag));
        }

        using var response = await httpClient.SendAsync(
            request,
            HttpCompletionOption.ResponseHeadersRead,
            cancellationToken).ConfigureAwait(false);
        if (response.StatusCode == HttpStatusCode.NotModified)
        {
            await PromotePendingAfterSuccessfulUseAsync(identity, cancellationToken).ConfigureAwait(false);
            return null;
        }

        if (await HandleRevocationAsync(response, cancellationToken).ConfigureAwait(false))
        {
            throw new AgentApiException(HttpStatusCode.Gone, AgentProblemCodes.AgentRevoked);
        }

        if (!response.IsSuccessStatusCode)
        {
            throw await CreateExceptionAsync(response, cancellationToken).ConfigureAwait(false);
        }

        var contentLength = response.Content.Headers.ContentLength;
        if (contentLength > MaximumConfigurationBytes)
        {
            throw new AgentApiException(HttpStatusCode.RequestEntityTooLarge, "configuration-too-large");
        }

        var tag = response.Headers.ETag?.ToString();
        EnsureStrongETag(tag);
        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        using var limited = new LengthLimitedReadStream(stream, MaximumConfigurationBytes);
        using var buffer = new MemoryStream();
        await limited.CopyToAsync(buffer, cancellationToken).ConfigureAwait(false);
        var payloadBytes = buffer.ToArray();
        AgentConfigurationResponse payload;
        try
        {
            payload = JsonSerializer.Deserialize<AgentConfigurationResponse>(payloadBytes, JsonOptions) ??
                      throw new AgentApiException(response.StatusCode, AgentProblemCodes.RequestInvalid);
        }
        catch (JsonException) when (TryGetConfigurationVersion(payloadBytes, out var rejectedVersion))
        {
            throw new AgentConfigurationPayloadException(rejectedVersion);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(payloadBytes);
        }

        EnsureSchema(payload.SchemaVersion);
        EnsureConfigurationETag(payload, tag!);
        await PromotePendingAfterSuccessfulUseAsync(identity, cancellationToken).ConfigureAwait(false);
        return (payload, tag!);
    }

    public async ValueTask<AgentConfigurationAcknowledgementResponse> AcknowledgeConfigurationAsync(
        AgentIdentity identity,
        AgentConfigurationAcknowledgementRequest acknowledgement,
        CancellationToken cancellationToken)
    {
        for (var attempt = 0; ; attempt++)
        {
            HttpResponseMessage response;
            try
            {
                using var request = CreateAuthenticatedRequest(
                    HttpMethod.Post,
                    $"api/v1/agents/{identity.AgentId:D}/configuration/acknowledgements",
                    identity.AuthenticationCredential.Secret,
                    acknowledgement);
                response = await httpClient.SendAsync(request, cancellationToken).ConfigureAwait(false);
            }
            catch (HttpRequestException) when (attempt < 2)
            {
                await retryDelay.DelayAsync(TimeSpan.FromSeconds(1 << attempt), cancellationToken).ConfigureAwait(false);
                continue;
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested && attempt < 2)
            {
                await retryDelay.DelayAsync(TimeSpan.FromSeconds(1 << attempt), cancellationToken).ConfigureAwait(false);
                continue;
            }

            using (response)
            {
                if (await HandleRevocationAsync(response, cancellationToken).ConfigureAwait(false))
                {
                    throw new AgentApiException(HttpStatusCode.Gone, AgentProblemCodes.AgentRevoked);
                }

                if (response.StatusCode == HttpStatusCode.Unauthorized && identity.PendingCredential is not null)
                {
                    identity = await DiscardPendingCredentialAsync(identity, cancellationToken).ConfigureAwait(false);
                    continue;
                }

                if (!response.IsSuccessStatusCode)
                {
                    if (attempt < 2 && IsRetryable(response.StatusCode))
                    {
                        await retryDelay.DelayAsync(GetRetryDelay(response, attempt), cancellationToken).ConfigureAwait(false);
                        continue;
                    }

                    throw await CreateExceptionAsync(response, cancellationToken).ConfigureAwait(false);
                }

                var payload = await ReadSuccessAsync<AgentConfigurationAcknowledgementResponse>(response, cancellationToken)
                    .ConfigureAwait(false);
                await PromotePendingAfterSuccessfulUseAsync(identity, cancellationToken).ConfigureAwait(false);
                EnsureSchema(payload.SchemaVersion);
                return payload;
            }
        }
    }

    public async ValueTask<AgentIdentity> RotateCredentialAsync(
        AgentIdentity identity,
        CancellationToken cancellationToken)
    {
        try
        {
            return await RotateCredentialCoreAsync(identity, cancellationToken).ConfigureAwait(false);
        }
        catch (AgentApiException exception) when (
            exception.StatusCode == HttpStatusCode.Unauthorized && identity.PendingCredential is not null)
        {
            identity = await DiscardPendingCredentialAsync(identity, cancellationToken).ConfigureAwait(false);
            return await RotateCredentialCoreAsync(identity, cancellationToken).ConfigureAwait(false);
        }
    }

    private async ValueTask<AgentIdentity> RotateCredentialCoreAsync(
        AgentIdentity identity,
        CancellationToken cancellationToken)
    {
        using var request = CreateAuthenticatedRequest(
            HttpMethod.Post,
            $"api/v1/agents/{identity.AgentId:D}/credentials/rotate",
            identity.AuthenticationCredential.Secret,
            new RotateAgentCredentialRequest(AgentContract.SchemaVersion));
        using var response = await httpClient.SendAsync(request, cancellationToken).ConfigureAwait(false);
        if (await HandleRevocationAsync(response, cancellationToken).ConfigureAwait(false))
        {
            throw new AgentApiException(HttpStatusCode.Gone, AgentProblemCodes.AgentRevoked);
        }

        var payload = await ReadSuccessAsync<RotateAgentCredentialResponse>(response, cancellationToken).ConfigureAwait(false);
        EnsureSchema(payload.SchemaVersion);
        var updated = identity with
        {
            PendingCredential = new AgentCredential(
                payload.CredentialId,
                payload.AgentCredential,
                payload.ExpiresAt,
                payload.RotateAfter),
        };
        await identityStore.SaveAsync(updated, cancellationToken).ConfigureAwait(false);
        return updated;
    }

    private async ValueTask<AgentIdentity> DiscardPendingCredentialAsync(
        AgentIdentity identity,
        CancellationToken cancellationToken)
    {
        var recovered = identity with { PendingCredential = null };
        await identityStore.SaveAsync(recovered, cancellationToken).ConfigureAwait(false);
        return recovered;
    }

    private static HttpRequestMessage CreateAuthenticatedRequest(
        HttpMethod method,
        string relativeUri,
        string credential)
    {
        var request = new HttpRequestMessage(method, relativeUri);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", credential);
        return request;
    }

    private static HttpRequestMessage CreateAuthenticatedRequest<T>(
        HttpMethod method,
        string relativeUri,
        string credential,
        T body)
        where T : notnull
    {
        var request = CreateAuthenticatedRequest(method, relativeUri, credential);
        request.Content = JsonContent.Create(body, options: JsonOptions);
        return request;
    }

    private async ValueTask PromotePendingAfterSuccessfulUseAsync(
        AgentIdentity identity,
        CancellationToken cancellationToken)
    {
        if (identity.PendingCredential is null)
        {
            return;
        }

        await identityStore.SaveAsync(identity with
        {
            ActiveCredential = identity.PendingCredential,
            PendingCredential = null,
        }, cancellationToken).ConfigureAwait(false);
    }

    private async ValueTask<bool> HandleRevocationAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        if (response.StatusCode != HttpStatusCode.Gone)
        {
            return false;
        }

        var code = await ReadProblemCodeAsync(response, cancellationToken).ConfigureAwait(false);
        if (!string.Equals(code, AgentProblemCodes.AgentRevoked, StringComparison.Ordinal))
        {
            return false;
        }

        await revocationHandler.HandleRevocationAsync(cancellationToken).ConfigureAwait(false);
        return true;
    }

    private static async ValueTask<T> ReadSuccessAsync<T>(
        HttpResponseMessage response,
        CancellationToken cancellationToken)
    {
        if (!response.IsSuccessStatusCode)
        {
            throw await CreateExceptionAsync(response, cancellationToken).ConfigureAwait(false);
        }

        return await response.Content.ReadFromJsonAsync<T>(JsonOptions, cancellationToken).ConfigureAwait(false) ??
               throw new AgentApiException(response.StatusCode, AgentProblemCodes.RequestInvalid);
    }

    private static async ValueTask<AgentApiException> CreateExceptionAsync(
        HttpResponseMessage response,
        CancellationToken cancellationToken) =>
        new(response.StatusCode, await ReadProblemCodeAsync(response, cancellationToken).ConfigureAwait(false));

    private static async ValueTask<string?> ReadProblemCodeAsync(
        HttpResponseMessage response,
        CancellationToken cancellationToken)
    {
        try
        {
            var problem = await response.Content.ReadFromJsonAsync<ProblemCode>(ProblemJsonOptions, cancellationToken)
                .ConfigureAwait(false);
            return problem?.Code;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static void EnsureSchema(int schemaVersion)
    {
        if (schemaVersion != AgentContract.SchemaVersion)
        {
            throw new AgentApiException(HttpStatusCode.BadRequest, AgentProblemCodes.SchemaUnsupported);
        }
    }

    private static void EnsureStrongETag(string? value)
    {
        if (string.IsNullOrWhiteSpace(value) || value.StartsWith("W/", StringComparison.OrdinalIgnoreCase) ||
            !EntityTagHeaderValue.TryParse(value, out _))
        {
            throw new AgentApiException(HttpStatusCode.BadRequest, "configuration-etag-invalid");
        }
    }

    private static void EnsureConfigurationETag(AgentConfigurationResponse configuration, string value)
    {
        var canonicalPayload = JsonSerializer.SerializeToUtf8Bytes(configuration, JsonOptions);
        try
        {
            var digest = SHA256.HashData(canonicalPayload);
            try
            {
                var expected = $"\"v{configuration.SchemaVersion}-{configuration.ConfigurationVersion}-" +
                               $"{Convert.ToHexStringLower(digest)}\"";
                if (!string.Equals(expected, value, StringComparison.Ordinal))
                {
                    throw new AgentApiException(HttpStatusCode.Conflict, "configuration-etag-mismatch");
                }
            }
            finally
            {
                CryptographicOperations.ZeroMemory(digest);
            }
        }
        finally
        {
            CryptographicOperations.ZeroMemory(canonicalPayload);
        }
    }

    private static bool TryGetConfigurationVersion(ReadOnlyMemory<byte> payload, out long configurationVersion)
    {
        configurationVersion = 0;
        try
        {
            using var document = JsonDocument.Parse(payload);
            return document.RootElement.ValueKind == JsonValueKind.Object &&
                   document.RootElement.TryGetProperty("configurationVersion", out var property) &&
                   property.TryGetInt64(out configurationVersion) &&
                   configurationVersion >= 1;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private static bool IsRetryable(HttpStatusCode statusCode) =>
        statusCode == HttpStatusCode.TooManyRequests || (int)statusCode >= 500;

    private static TimeSpan GetRetryDelay(HttpResponseMessage response, int attempt)
    {
        var retryAfter = response.Headers.RetryAfter?.Delta;
        return retryAfter.HasValue && retryAfter.Value > TimeSpan.Zero && retryAfter.Value <= TimeSpan.FromSeconds(30)
            ? retryAfter.Value
            : TimeSpan.FromSeconds(1 << attempt);
    }

    private sealed record ProblemCode(string? Code);

    private sealed class LengthLimitedReadStream(Stream inner, long maximumBytes) : Stream
    {
        private long bytesRead;

        public override bool CanRead => inner.CanRead;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => bytesRead; set => throw new NotSupportedException(); }
        public override void Flush() => throw new NotSupportedException();
        public override int Read(byte[] buffer, int offset, int count) => Track(inner.Read(buffer, offset, count));
        public override int Read(Span<byte> buffer) => Track(inner.Read(buffer));
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) =>
            Track(await inner.ReadAsync(buffer, cancellationToken).ConfigureAwait(false));
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                inner.Dispose();
            }

            base.Dispose(disposing);
        }

        public override async ValueTask DisposeAsync()
        {
            await inner.DisposeAsync().ConfigureAwait(false);
            await base.DisposeAsync().ConfigureAwait(false);
            GC.SuppressFinalize(this);
        }

        private int Track(int count)
        {
            bytesRead += count;
            if (bytesRead > maximumBytes)
            {
                throw new AgentApiException(HttpStatusCode.RequestEntityTooLarge, "configuration-too-large");
            }

            return count;
        }
    }
}
