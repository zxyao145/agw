namespace Agw.Extensions.Sdk;

public interface IExtensionLeaseManager
{
    ValueTask<IExtensionLease> AcquireAsync(string name, CancellationToken cancellationToken = default);
}

public interface IExtensionLease : IAsyncDisposable
{
    CancellationToken HandleLostToken { get; }
}

public interface IExtensionSecrets
{
    string Protect(string tableName, Guid recordId, string plaintext);

    string Unprotect(string tableName, Guid recordId, string protectedValue);
}
