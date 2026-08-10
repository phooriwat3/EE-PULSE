using EePulse.Agent;
using Serilog;
using Serilog.Formatting.Compact;

var builder = Host.CreateApplicationBuilder(args);
builder.Services.AddWindowsService(options => options.ServiceName = "EE Pulse Probe Agent");
builder.Services.AddSerilog(configuration => configuration
    .Enrich.FromLogContext()
    .WriteTo.Console(new CompactJsonFormatter()));
builder.Services.AddHostedService<AgentHost>();

await builder.Build().RunAsync();
