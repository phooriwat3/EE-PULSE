using System.Runtime.Versioning;
using System.Security.AccessControl;
using System.Security.Principal;

namespace EePulse.Agent.Infrastructure.Storage;

public sealed class WindowsServiceFileAccessPolicy(string serviceIdentity) : IProtectedFileAccessPolicy
{
    public void SecureDirectory(string path)
    {
        if (OperatingSystem.IsWindows())
        {
            SecureDirectoryWindows(path);
            return;
        }

        throw new PlatformNotSupportedException("Windows ACL protection is required.");
    }

    public void SecureFile(string path)
    {
        if (OperatingSystem.IsWindows())
        {
            SecureFileWindows(path);
            return;
        }

        throw new PlatformNotSupportedException("Windows ACL protection is required.");
    }

    [SupportedOSPlatform("windows")]
    private void SecureDirectoryWindows(string path)
    {
        Directory.CreateDirectory(path);
        var security = new DirectorySecurity();
        security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
        AddRules(security, FileSystemRights.FullControl, InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit);
        new DirectoryInfo(path).SetAccessControl(security);
    }

    [SupportedOSPlatform("windows")]
    private void SecureFileWindows(string path)
    {
        var security = new FileSecurity();
        security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
        AddRules(security, FileSystemRights.FullControl, InheritanceFlags.None);
        new FileInfo(path).SetAccessControl(security);
    }

    [SupportedOSPlatform("windows")]
    private void AddRules(FileSystemSecurity security, FileSystemRights rights, InheritanceFlags inheritanceFlags)
    {
        var administrators = new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null);
        var service = new NTAccount(serviceIdentity).Translate(typeof(SecurityIdentifier));
        security.AddAccessRule(new FileSystemAccessRule(
            administrators,
            rights,
            inheritanceFlags,
            PropagationFlags.None,
            AccessControlType.Allow));
        security.AddAccessRule(new FileSystemAccessRule(
            service,
            rights,
            inheritanceFlags,
            PropagationFlags.None,
            AccessControlType.Allow));
        security.SetOwner(administrators);
    }

}
