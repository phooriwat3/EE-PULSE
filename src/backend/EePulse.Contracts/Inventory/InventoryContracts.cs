namespace EePulse.Contracts.Inventory;

public sealed record PagedResponse<T>(IReadOnlyList<T> Items, int Page, int PageSize, long TotalCount);

public sealed record SiteResponse(
    string Id, string Code, string Name, string Timezone, bool Enabled,
    DateTimeOffset CreatedAt, DateTimeOffset UpdatedAt, long RowVersion);

public sealed record CreateSiteRequest(string Code, string Name, string Timezone);
public sealed record UpdateSiteRequest(string Code, string Name, string Timezone, bool Enabled, long RowVersion);

public sealed record AgentGroupResponse(
    string Id, string Name, string? Description, bool Enabled,
    DateTimeOffset CreatedAt, DateTimeOffset UpdatedAt, long RowVersion);

public sealed record CreateAgentGroupRequest(string Name, string? Description);
public sealed record UpdateAgentGroupRequest(string Name, string? Description, bool Enabled, long RowVersion);

public sealed record DeviceResponse(
    string Id, string SiteId, string Name, string Address, string? Hostname, string DeviceType,
    string? Area, string? Owner, string Criticality, string[] Tags, bool Enabled,
    DateTimeOffset CreatedAt, DateTimeOffset UpdatedAt, long RowVersion);

public sealed record CreateDeviceRequest(
    string SiteId, string Name, string Address, string? Hostname, string DeviceType,
    string? Area, string? Owner, string Criticality, string[] Tags);

public sealed record UpdateDeviceRequest(
    string SiteId, string Name, string Address, string? Hostname, string DeviceType,
    string? Area, string? Owner, string Criticality, string[] Tags, bool Enabled, long RowVersion);

public sealed record ProbeResponse(
    string Id, string DeviceId, string AgentGroupId, string Type, int IntervalSeconds,
    int TimeoutMilliseconds, int AttemptCount, int? WarningRttMilliseconds,
    int? CriticalRttMilliseconds, int FailureThreshold, int RecoveryThreshold,
    bool Enabled, long ConfigVersion, long RowVersion);

public sealed record CreateProbeRequest(
    string DeviceId, string AgentGroupId, int IntervalSeconds, int TimeoutMilliseconds,
    int AttemptCount, int? WarningRttMilliseconds, int? CriticalRttMilliseconds,
    int FailureThreshold, int RecoveryThreshold);

public sealed record UpdateProbeRequest(
    string AgentGroupId, int IntervalSeconds, int TimeoutMilliseconds, int AttemptCount,
    int? WarningRttMilliseconds, int? CriticalRttMilliseconds, int FailureThreshold,
    int RecoveryThreshold, bool Enabled, long RowVersion);

public sealed record MaintenanceWindowResponse(
    string Id, string Name, DateTimeOffset StartsAt, DateTimeOffset EndsAt, string Timezone,
    string? SiteId, string? DeviceId, string? ProbeId, bool Enabled,
    DateTimeOffset CreatedAt, DateTimeOffset UpdatedAt, long RowVersion);

public sealed record CreateMaintenanceWindowRequest(
    string Name, DateTimeOffset StartsAt, DateTimeOffset EndsAt, string Timezone,
    string? SiteId, string? DeviceId, string? ProbeId);

public sealed record UpdateMaintenanceWindowRequest(
    string Name, DateTimeOffset StartsAt, DateTimeOffset EndsAt, string Timezone,
    string? SiteId, string? DeviceId, string? ProbeId, bool Enabled, long RowVersion);

public sealed record CsvImportError(string Field, string Code, string Message);
public sealed record CsvImportPreviewRow(int RowNumber, DeviceImportRow? Normalized, IReadOnlyList<CsvImportError> Errors);
public sealed record DeviceImportRow(
    string SiteCode, string Name, string Address, string? Hostname, string DeviceType,
    string? Area, string? Owner, string Criticality, string[] Tags);
public sealed record CsvImportPreviewResponse(
    string PreviewToken, DateTimeOffset ExpiresAt, int TotalRows, int ValidRows, int InvalidRows,
    IReadOnlyList<CsvImportPreviewRow> Rows);
public sealed record CsvImportCommitRequest(string PreviewToken);
public sealed record CsvImportCommitResponse(
    string PreviewToken, int Created, int Skipped, IReadOnlyList<string> DeviceIds,
    IReadOnlyList<CsvImportPreviewRow> Errors, bool AlreadyCommitted);
