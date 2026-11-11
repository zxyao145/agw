using Agw.Extensions.Sdk;
using Agw.Shared.Contracts.Coordination;
using Microsoft.AspNetCore.DataProtection;

namespace Agw.Extensions.Infrastructure.Runtime;

internal sealed class ExtensionLeaseManager : IExtensionLeaseManager
{
    private readonly Guid _extensionId;
    private readonly IApplicationLock _applicationLock;

    public ExtensionLeaseManager(Guid extensionId, IApplicationLock applicationLock)
    {
        _extensionId = extensionId;
        _applicationLock = applicationLock;
    }

    public async ValueTask<IExtensionLease> AcquireAsync(string name, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        IApplicationLockLease lease = await _applicationLock.AcquireAsync(
            $"ext:{_extensionId:D}:{name}",
            cancellationToken
        );
        return new Lease(lease);
    }

    private sealed class Lease : IExtensionLease
    {
        private readonly IApplicationLockLease _lease;

        public Lease(IApplicationLockLease lease)
        {
            _lease = lease;
        }

        public CancellationToken HandleLostToken => _lease.HandleLostToken;

        public ValueTask DisposeAsync() => _lease.DisposeAsync();
    }
}

internal sealed class ExtensionSecrets : IExtensionSecrets
{
    private readonly Guid _extensionId;
    private readonly IDataProtectionProvider _provider;

    public ExtensionSecrets(Guid extensionId, IDataProtectionProvider provider)
    {
        _extensionId = extensionId;
        _provider = provider;
    }

    public string Protect(string tableName, Guid recordId, string plaintext)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(tableName);
        ArgumentException.ThrowIfNullOrWhiteSpace(plaintext);
        return CreateProtector(tableName, recordId).Protect(plaintext);
    }

    public string Unprotect(string tableName, Guid recordId, string protectedValue)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(tableName);
        ArgumentException.ThrowIfNullOrWhiteSpace(protectedValue);
        return CreateProtector(tableName, recordId).Unprotect(protectedValue);
    }

    private IDataProtector CreateProtector(string tableName, Guid recordId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(tableName);
        ArgumentOutOfRangeException.ThrowIfEqual(recordId, Guid.Empty);
        return _provider
            .CreateProtector("Agw.Extensions.Secrets")
            .CreateProtector(_extensionId.ToString("N"))
            .CreateProtector(tableName)
            .CreateProtector(recordId.ToString("N"));
    }
}
