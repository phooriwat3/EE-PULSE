namespace EePulse.Api.Authorization;

public static class InventoryAuthorization
{
    public const string ReadPolicy = "inventory.read";
    public const string WritePolicy = "inventory.write";
    public const string AdminPolicy = "inventory.admin";
    public const string AgentReadPolicy = "agents.read";
    public const string AgentAdminPolicy = "agents.admin";

    public static IServiceCollection AddInventoryAuthorization(this IServiceCollection services)
    {
        services.AddAuthorizationBuilder()
            .AddPolicy(ReadPolicy, policy => policy.RequireRole("Viewer", "Operator", "Engineer", "Administrator", "Auditor"))
            .AddPolicy(WritePolicy, policy => policy.RequireRole("Engineer", "Administrator"))
            .AddPolicy(AdminPolicy, policy => policy.RequireRole("Administrator"));
        services.AddAuthorizationBuilder()
            .AddPolicy(AgentReadPolicy, policy => policy.RequireRole("Viewer", "Operator", "Engineer", "Administrator", "Auditor"))
            .AddPolicy(AgentAdminPolicy, policy => policy.RequireRole("Administrator"));
        return services;
    }
}
