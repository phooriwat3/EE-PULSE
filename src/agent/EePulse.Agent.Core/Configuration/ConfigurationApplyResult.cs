namespace EePulse.Agent.Core.Configuration;

public enum ConfigurationRejectionCode
{
    SchemaUnsupported,
    IdentityMismatch,
    VersionNotMonotonic,
    NetworkPolicyMismatch,
    ProbeInvalid,
    DuplicateProbe,
    ConfigurationTooLarge,
    ConfigurationStorageFailed,
    SchedulerApplyFailed,
}

public sealed record ConfigurationApplyResult(
    bool Applied,
    long ActiveVersion,
    ConfigurationRejectionCode? RejectionCode)
{
    public static ConfigurationApplyResult Success(long version) => new(true, version, null);

    public static ConfigurationApplyResult Rejected(long activeVersion, ConfigurationRejectionCode code) =>
        new(false, activeVersion, code);
}
