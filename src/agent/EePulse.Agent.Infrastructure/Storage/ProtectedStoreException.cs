namespace EePulse.Agent.Infrastructure.Storage;

public sealed class ProtectedStoreException : Exception
{
    public ProtectedStoreException()
        : base("The protected Agent state could not be read or written.")
    {
    }

    public ProtectedStoreException(Exception innerException)
        : base("The protected Agent state could not be read or written.", innerException)
    {
    }
}
