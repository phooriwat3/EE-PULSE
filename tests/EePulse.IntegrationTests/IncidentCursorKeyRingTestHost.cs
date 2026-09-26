using System.Security.Cryptography;
using EePulse.Api.Dashboard;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace EePulse.IntegrationTests;

internal static class IncidentCursorKeyRingTestHost
{
    internal static IIncidentCursorKeyRing CreateRing() => IncidentCursorKeyRing.Parse("test",
        "test=" + Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)));

    internal static WebApplicationFactory<Program> WithIncidentCursorKeyRing(
        this WebApplicationFactory<Program> factory, IIncidentCursorKeyRing ring) =>
        factory.WithWebHostBuilder(builder => builder.ConfigureTestServices(services =>
        {
            services.RemoveAll<IIncidentCursorKeyRing>();
            services.AddSingleton(ring);
        }));
}
