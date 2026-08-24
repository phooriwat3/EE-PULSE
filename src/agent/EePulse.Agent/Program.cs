using EePulse.Agent;
using EePulse.Agent.Core.Configuration;
using EePulse.Agent.Core.Identity;
using EePulse.Agent.Core.Outbox;
using EePulse.Agent.Core.Probing;
using EePulse.Agent.Core.Runtime;
using EePulse.Agent.Core.Security;
using EePulse.Agent.Core.Transport;
using EePulse.Agent.Infrastructure.Security;
using EePulse.Agent.Infrastructure.Storage;
using Serilog;
using Serilog.Formatting.Compact;

var builder = Host.CreateApplicationBuilder(args);
var isProduction = builder.Environment.IsProduction();
var serviceIdentity = builder.Configuration["Agent:ServiceIdentity"];
var serverAddress = builder.Configuration["Agent:ServerBaseAddress"];
if (string.IsNullOrWhiteSpace(serviceIdentity) || string.IsNullOrWhiteSpace(serverAddress) ||
    !Uri.TryCreate(serverAddress, UriKind.Absolute, out var serverUri))
{
    throw new InvalidOperationException("Agent identity and server settings are required.");
}

var configuredStorage = builder.Configuration["Agent:StorageDirectory"];
var storageOptions = string.IsNullOrWhiteSpace(configuredStorage)
    ? AgentStorageOptions.CreateDefault(serviceIdentity, isProduction)
    : new AgentStorageOptions(configuredStorage, serviceIdentity, isProduction);
storageOptions.Validate();
var clientOptions = new AgentClientOptions(serverUri, isProduction);
clientOptions.Validate();

builder.Services.AddWindowsService(options => options.ServiceName = "EE Pulse Probe Agent");
builder.Services.Configure<HostOptions>(options =>
{
    options.BackgroundServiceExceptionBehavior = BackgroundServiceExceptionBehavior.StopHost;
    options.ShutdownTimeout = TimeSpan.FromSeconds(30);
});
builder.Services.AddSerilog(configuration => configuration
    .Enrich.FromLogContext()
    .WriteTo.Console(new CompactJsonFormatter()));
builder.Services.AddSingleton(storageOptions);
builder.Services.AddSingleton(clientOptions);
builder.Services.AddSingleton<TimeProvider>(TimeProvider.System);
builder.Services.AddSingleton<ISecretProtector, DpapiLocalMachineProtector>();
builder.Services.AddSingleton<IProtectedFileAccessPolicy>(_ => new WindowsServiceFileAccessPolicy(serviceIdentity));
builder.Services.AddSingleton<ProtectedAgentIdentityStore>();
builder.Services.AddSingleton<IAgentIdentityStore>(provider => provider.GetRequiredService<ProtectedAgentIdentityStore>());
builder.Services.AddSingleton<ProtectedAgentConfigurationStore>();
builder.Services.AddSingleton<IAgentConfigurationStore>(provider => provider.GetRequiredService<ProtectedAgentConfigurationStore>());
builder.Services.AddSingleton<ProtectedPendingAcknowledgementStore>();
builder.Services.AddSingleton<IPendingAcknowledgementStore>(provider => provider.GetRequiredService<ProtectedPendingAcknowledgementStore>());
builder.Services.AddSingleton<IProbeResultOutbox>(provider => new SqliteProbeResultOutbox(Path.Combine(storageOptions.RootDirectory, "probe-results.db")));
builder.Services.AddAgentProbeRuntime();
builder.Services.AddSingleton<IAgentSelfStatus, DefaultAgentSelfStatus>();
builder.Services.AddSingleton<IAgentRevocationHandler, AgentRevocationHandler>();
builder.Services.AddSingleton<IAgentRetryDelay>(provider => new AgentRetryDelay(provider.GetRequiredService<TimeProvider>()));
builder.Services.AddSingleton<IAgentRuntimeDelay>(provider => new AgentRuntimeDelay(provider.GetRequiredService<TimeProvider>()));
builder.Services.AddSingleton<IProbeResultDeliveryDelay>(provider => new ProbeResultDeliveryDelay(provider.GetRequiredService<TimeProvider>()));
builder.Services.AddSingleton<IProbeResultDeliveryRandom, ProbeResultDeliveryRandom>();
builder.Services.AddSingleton(_ => new HttpClient());
builder.Services.AddSingleton<AgentApiClient>();
builder.Services.AddSingleton<ProbeResultDeliveryCoordinator>();
builder.Services.AddSingleton(_ => new AgentConfigurationValidator(builder.Environment.IsDevelopment()));
builder.Services.AddSingleton<AgentConfigurationApplier>();
builder.Services.AddSingleton(provider => new AgentRuntime(
    provider.GetRequiredService<AgentApiClient>(),
    provider.GetRequiredService<IAgentIdentityStore>(),
    provider.GetRequiredService<IAgentConfigurationStore>(),
    provider.GetRequiredService<IPendingAcknowledgementStore>(),
    provider.GetRequiredService<AgentConfigurationApplier>(),
    provider.GetRequiredService<IAgentSelfStatus>(),
    provider.GetRequiredService<TimeProvider>(),
    builder.Environment.IsDevelopment(),
    provider.GetRequiredService<IAgentRuntimeDelay>()));
builder.Services.AddHostedService<AgentHost>();
builder.Services.AddHostedService<ProbeResultDeliveryHost>();

await builder.Build().RunAsync();
