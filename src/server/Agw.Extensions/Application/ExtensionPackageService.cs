using Agw.Extensions.Application.Packages;
using Agw.Extensions.Application.Persistence;
using Agw.Extensions.Contracts;
using Agw.Shared.Contracts;
using Agw.Shared.Exceptions;

namespace Agw.Extensions.Application;

public sealed class ExtensionPackageService
{
    private readonly IExtensionPackageCatalog _catalog;
    private readonly IExtensionEnablementStore _enablement;
    private readonly IExtensionRuntime _runtime;
    private readonly ICurrentUser _user;

    public ExtensionPackageService(
        IExtensionPackageCatalog catalog,
        IExtensionEnablementStore enablement,
        IExtensionRuntime runtime,
        ICurrentUser user
    )
    {
        _catalog = catalog;
        _enablement = enablement;
        _runtime = runtime;
        _user = user;
    }

    public async Task<(long Revision, IReadOnlyList<ExtensionCatalogEntry> Entries)> GetCatalogAsync(
        long? afterRevision,
        CancellationToken cancellationToken
    )
    {
        cancellationToken.ThrowIfCancellationRequested();
        _ = _user.RequiredUserId;
        var active = await GetActiveDesktopExtensionsAsync(cancellationToken);
        return _catalog.GetDesktopCatalog(afterRevision, active);
    }

    public async Task<Stream> DownloadAsync(Guid extensionId, string releaseHash, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        _ = _user.RequiredUserId;
        if (!await IsActiveDesktopExtensionAsync(extensionId, cancellationToken))
            throw new AgwException(ErrorCodes.ExtensionNotFound);
        var package = await _catalog.GetDesktopPackageAsync(extensionId, releaseHash, cancellationToken);
        try
        {
            if (!await IsActiveDesktopExtensionAsync(extensionId, cancellationToken))
                throw new AgwException(ErrorCodes.ExtensionNotFound);
            return package;
        }
        catch
        {
            await package.DisposeAsync();
            throw;
        }
    }

    private async Task<HashSet<Guid>> GetActiveDesktopExtensionsAsync(CancellationToken cancellationToken)
    {
        var active = new HashSet<Guid>();
        var snapshots = _runtime.GetSnapshots().ToDictionary(snapshot => snapshot.GenerationId.ExtensionId);
        foreach (
            var package in _catalog
                .GetEntries()
                .Where(package => package.Manifest.Targets.HasFlag(ExtensionTarget.Desktop))
        )
        {
            var enablement = await _enablement.GetAsync(package.ExtensionId, cancellationToken);
            if (
                enablement.Enabled
                && snapshots.TryGetValue(package.ExtensionId, out var snapshot)
                && snapshot.State == ExtensionGenerationState.Active
                && snapshot.Epoch == enablement.Version
                && snapshot.ReleaseHash == package.ReleaseHash
            )
                active.Add(package.ExtensionId);
        }
        return active;
    }

    private async Task<bool> IsActiveDesktopExtensionAsync(Guid extensionId, CancellationToken cancellationToken)
    {
        var package = _catalog.GetEntries().SingleOrDefault(entry => entry.ExtensionId == extensionId);
        if (package is null || !package.Manifest.Targets.HasFlag(ExtensionTarget.Desktop))
            return false;

        var enablement = await _enablement.GetAsync(extensionId, cancellationToken);
        if (!enablement.Enabled)
            return false;

        var snapshot = _runtime.GetSnapshots().SingleOrDefault(item => item.GenerationId.ExtensionId == extensionId);
        return snapshot is not null
            && snapshot.State == ExtensionGenerationState.Active
            && snapshot.Epoch == enablement.Version
            && snapshot.ReleaseHash == package.ReleaseHash;
    }
}
