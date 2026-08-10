using System.Reflection;
using EePulse.Api.Middleware;
using EePulse.Application.Time;
using EePulse.Contracts;
using EePulse.Contracts.Health;
using EePulse.Infrastructure;
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
    builder.Services.AddOpenApi("v1");
    builder.Services.AddHealthChecks();
    builder.Services.AddEePulseInfrastructure();

    var app = builder.Build();

    app.UseMiddleware<CorrelationIdMiddleware>();
    app.UseSerilogRequestLogging();
    app.UseExceptionHandler();
    app.UseStatusCodePages();

    app.MapOpenApi("/openapi/{documentName}.json");

    app.MapGet("/health/live", (IUtcClock clock) => CreateHealthResponse(clock, "live"))
        .WithName("GetLiveness")
        .WithTags("Platform")
        .Produces<HealthResponse>();

    app.MapGet("/health/ready", (IUtcClock clock) => CreateHealthResponse(clock, "ready"))
        .WithName("GetReadiness")
        .WithTags("Platform")
        .Produces<HealthResponse>();

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

public partial class Program;
