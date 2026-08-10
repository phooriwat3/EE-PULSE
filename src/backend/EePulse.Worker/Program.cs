using EePulse.Infrastructure;
using EePulse.Worker;
using Serilog;
using Serilog.Formatting.Compact;

var builder = Host.CreateApplicationBuilder(args);
builder.Services.AddSerilog(configuration => configuration
    .Enrich.FromLogContext()
    .WriteTo.Console(new CompactJsonFormatter()));
builder.Services.AddEePulseInfrastructure();
builder.Services.AddHostedService<WorkerHost>();

await builder.Build().RunAsync();
