namespace EePulse.Api.Authorization;

public static class DashboardAuthorization
{
    public const string DashboardReadPolicy = "dashboard.read";
    public const string IncidentsReadPolicy = "incidents.read";
    public const string IncidentsOperatePolicy = "incidents.operate";
    public const string AuditReadPolicy = "audit.read";

    private static readonly string[] DashboardReadRoles = ["Viewer", "Operator", "Engineer", "Administrator", "Auditor"];

    public static IServiceCollection AddDashboardAuthorization(this IServiceCollection services)
    {
        services.AddAuthorizationBuilder()
            .AddPolicy(DashboardReadPolicy, policy => policy.RequireAuthenticatedUser().RequireRole(DashboardReadRoles))
            .AddPolicy(IncidentsReadPolicy, policy => policy.RequireAuthenticatedUser().RequireRole(DashboardReadRoles))
            .AddPolicy(IncidentsOperatePolicy, policy => policy.RequireAuthenticatedUser().RequireRole("Operator", "Administrator"))
            .AddPolicy(AuditReadPolicy, policy => policy.RequireAuthenticatedUser().RequireRole("Auditor", "Administrator"));
        return services;
    }
}
