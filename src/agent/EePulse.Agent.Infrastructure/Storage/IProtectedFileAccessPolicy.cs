namespace EePulse.Agent.Infrastructure.Storage;

public interface IProtectedFileAccessPolicy
{
    void SecureDirectory(string path);

    void SecureFile(string path);
}
