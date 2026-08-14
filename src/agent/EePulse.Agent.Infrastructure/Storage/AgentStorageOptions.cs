namespace EePulse.Agent.Infrastructure.Storage;

public sealed record AgentStorageOptions(string RootDirectory, string ServiceIdentity, bool IsProduction)
{
    public static AgentStorageOptions CreateDefault(string serviceIdentity, bool isProduction) =>
        new(
            Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
                "EE Pulse",
                "Agent"),
            serviceIdentity,
            isProduction);

    public void Validate()
    {
        if (string.IsNullOrWhiteSpace(RootDirectory) || !Path.IsPathFullyQualified(RootDirectory) ||
            string.IsNullOrWhiteSpace(ServiceIdentity))
        {
            throw new InvalidOperationException("Agent protected storage configuration is invalid.");
        }

        if (!IsProduction)
        {
            return;
        }

        if (!OperatingSystem.IsWindows())
        {
            throw new InvalidOperationException("Production Agent protected storage requires Windows.");
        }

        var programData = Path.GetFullPath(
            Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData))
            .TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        var root = Path.GetFullPath(RootDirectory).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        if (!root.StartsWith(programData, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("Production Agent storage must be located under ProgramData.");
        }
    }
}
