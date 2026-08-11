using System.Reflection;
using EePulse.Api.Middleware;
using EePulse.Api.Authorization;
using EePulse.Api.Inventory;
using EePulse.Api.Agents;
using EePulse.Api.OpenApi;
using EePulse.Application.Time;
using EePulse.Contracts;
using EePulse.Contracts.Health;
using EePulse.Infrastructure;
using EePulse.Infrastructure.Persistence;
using Microsoft.AspNetCore.Authentication;
using Microsoft.EntityFrameworkCore;
using Microsoft.AspNetCore.HttpOverrides;
using System.Net;
using Serilog;
using Serilog.Formatting.Compact;

Log.Logger = new LoggerConfiguration()
    .Enrich.FromLogContext()
    .WriteTo.Console(new CompactJsonFormatter())
    .CreateBootstrapLogger();

try
{
    var builder = WebApplication.CreateBuilder(args);

    builder.Services.AddSerilog((_, configuration) => configuration
        .Enrich.FromLogContext()
        .WriteTo.Console(new CompactJsonFormatter()));
    builder.Services.AddProblemDetails();
    builder.Services.ConfigureHttpJsonOptions(options => EePulse.Contracts.Agents.AgentJsonContract.AddConverters(options.SerializerOptions));
    builder.Services.AddOpenApi("v1", options =>
        options.AddDocumentTransformer<InventorySecurityDocumentTransformer>());
    builder.Services.AddHealthChecks();
    builder.Services.AddEePulseInfrastructure();
    builder.Services.AddAuthentication(DevelopmentAuthenticationHandler.SchemeName)
        .AddScheme<AuthenticationSchemeOptions, DevelopmentAuthenticationHandler>(
            DevelopmentAuthenticationHandler.SchemeName, _ => { })
        .AddScheme<AuthenticationSchemeOptions, AgentCredentialAuthenticationHandler>(
            EePulse.Contracts.Agents.AgentContract.CredentialAuthenticationScheme, _ => { });
    builder.Services.AddInventoryAuthorization();
    builder.Services.AddSingleton<DeviceCsvImportService>();
    builder.Services.AddHostedService<AgentOfflineService>();
    builder.Services.AddSingleton<AgentRateLimiter>();
    builder.Services.AddSingleton(new AgentRateLimitDefaults());

    var knownProxyValues = builder.Configuration.GetSection("AgentIdentity:KnownProxies").Get<string[]>() ?? [];
    var knownProxies = knownProxyValues.Select(value => IPAddress.TryParse(value, out var address)
        ? address : throw new InvalidOperationException("Every AgentIdentity:KnownProxies entry must be a valid IP address.")).ToArray();
    if (!builder.Environment.IsDevelopment() &&
        (!builder.Configuration.GetValue<bool>("AgentIdentity:Enabled") ||
         !builder.Configuration.GetValue<bool>("AgentIdentity:TrustedHttpsProxy") || knownProxies.Length == 0))
    {
        throw new InvalidOperationException("Production Agent identity requires AgentIdentity:Enabled and trusted HTTPS proxy configuration.");
    }
    builder.Services.Configure<ForwardedHeadersOptions>(options =>
    {
        options.ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto;
        options.KnownIPNetworks.Clear(); options.KnownProxies.Clear();
        foreach (var address in knownProxies) options.KnownProxies.Add(address);
    });

    var postgresConnection = builder.Configuration.GetConnectionString("Postgres");
    if (!string.IsNullOrWhiteSpace(postgresConnection))
    {
        builder.Services.AddEePulsePersistence(builder.Configuration);
    }
    else if (!builder.Environment.IsDevelopment())
    {
        throw new InvalidOperationException("ConnectionStrings:Postgres is required outside Development.");
    }
    else
    {
        builder.Services.AddDbContext<EePulseDbContext>(options => options.UseNpgsql(
            "Host=127.0.0.1;Port=1;Database=unconfigured;Username=unconfigured;Timeout=1"));
    }

    var app = builder.Build();

    if (app.Environment.IsDevelopment() && !string.IsNullOrWhiteSpace(postgresConnection))
    {
        await using var scope = app.Services.CreateAsyncScope();
        var database = scope.ServiceProvider.GetRequiredService<EePulseDbContext>();
        await database.Database.MigrateAsync();
        if (builder.Configuration.GetValue<bool>("Inventory:SeedDevelopmentData"))
        {
            await DevelopmentInventorySeeder.SeedAsync(database, scope.ServiceProvider.GetRequiredService<IUtcClock>());
        }
    }

    app.UseForwardedHeaders();
    app.UseMiddleware<CorrelationIdMiddleware>();
    app.UseExceptionHandler();
    app.UseSerilogRequestLogging();
    app.UseMiddleware<AgentRequestSecurityMiddleware>();
    app.UseStatusCodePages();
    app.UseAuthentication();
    app.UseMiddleware<AgentRateLimitMiddleware>();
    app.UseAuthorization();
    if (string.IsNullOrWhiteSpace(postgresConnection))
    {
        app.Use(async (context, next) =>
        {
            if (context.Request.Path.StartsWithSegments("/api"))
            {
                await Results.Problem(
                    "PostgreSQL persistence is not configured.",
                    statusCode: StatusCodes.Status503ServiceUnavailable,
                    title: "Dependency unavailable").ExecuteAsync(context);
                return;
            }

            await next(context);
        });
    }

    app.MapOpenApi("/openapi/{documentName}.json");

    app.MapGet("/health/live", (IUtcClock clock) => CreateHealthResponse(clock, "live"))
        .WithName("GetLiveness")
        .WithTags("Platform")
        .Produces<HealthResponse>();

    app.MapGet("/health/ready", async (IUtcClock clock, HttpContext context, CancellationToken cancellationToken) =>
        await CreateReadinessResponse(clock, context, !string.IsNullOrWhiteSpace(postgresConnection), cancellationToken))
        .WithName("GetReadiness")
        .WithTags("Platform")
        .Produces<HealthResponse>();

    app.MapInventoryEndpoints();
    app.MapAgentEndpoints();

    app.Run();
}
catch (Exception exception)
{
    Log.Fatal(exception, "EE Pulse API terminated unexpectedly");
    throw;
}
finally
{
    await Log.CloseAndFlushAsync();
}

static HealthResponse CreateHealthResponse(IUtcClock clock, string status)
{
    var version = Assembly.GetExecutingAssembly().GetName().Version?.ToString() ?? "unknown";
    return new HealthResponse(ApiVersions.Current, "ee-pulse-api", status, clock.UtcNow, version);
}

static async Task<IResult> CreateReadinessResponse(
    IUtcClock clock,
    HttpContext context,
    bool persistenceConfigured,
    CancellationToken cancellationToken)
{
    if (!persistenceConfigured)
    {
        return Results.Json(CreateHealthResponse(clock, "not-ready"), statusCode: StatusCodes.Status503ServiceUnavailable);
    }

    var database = context.RequestServices.GetService<EePulseDbContext>();
    if (database is null || !await database.Database.CanConnectAsync(cancellationToken) ||
        (await database.Database.GetPendingMigrationsAsync(cancellationToken)).Any())
    {
        return Results.Json(CreateHealthResponse(clock, "not-ready"), statusCode: StatusCodes.Status503ServiceUnavailable);
    }

    return Results.Ok(CreateHealthResponse(clock, "ready"));
}

public partial class Program;
