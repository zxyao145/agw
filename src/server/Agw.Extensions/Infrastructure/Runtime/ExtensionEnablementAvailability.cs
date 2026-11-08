using Agw.Agents.Contracts.Execution;
using Agw.Extensions.Application.Persistence;
using Agw.Extensions.Contracts;
using Agw.Extensions.Infrastructure.Packages;
using Microsoft.Extensions.DependencyInjection;

namespace Agw.Extensions.Infrastructure.Runtime;

public sealed class ExtensionEnablementAvailability : IExtensionRuntimeAvailability
{
    private readonly IExtensionEnablementStore _enablement;
    private readonly ExtensionPackageCatalog _catalog;
    private readonly IServiceScopeFactory _scopes;

    public ExtensionEnablementAvailability(
        IExtensionEnablementStore enablement,
        ExtensionPackageCatalog catalog,
        IServiceScopeFactory scopes
    )
    {
        _enablement = enablement;
        _catalog = catalog;
        _scopes = scopes;
    }

    public async ValueTask<bool> CanAcceptNewWorkAsync(
        ExtensionExecutionPin pin,
        CancellationToken cancellationToken = default
    )
    {
        var state = await _enablement.GetAsync(pin.ExtensionId, cancellationToken);
        return state.Enabled && state.Version == pin.Epoch && HasRelease(pin.ExtensionId, pin.ReleaseHash);
    }

    public ValueTask<bool> CanContinuePinnedWorkAsync(
        ExtensionExecutionPin pin,
        CancellationToken cancellationToken = default
    )
    {
        cancellationToken.ThrowIfCancellationRequested();
        return ValueTask.FromResult(HasRelease(pin.ExtensionId, pin.ReleaseHash));
    }

    public async ValueTask<bool> CanContinueExecutionAsync(
        Guid extensionId,
        CancellationToken cancellationToken = default
    )
    {
        if (!_catalog.GetEntries().Any(entry => entry.ExtensionId == extensionId))
            return false;

        await using var scope = _scopes.CreateAsyncScope();
        return await scope
            .ServiceProvider.GetRequiredService<IExtensionExecutionUsage>()
            .HasNonterminalExecutionsAsync(extensionId, cancellationToken);
    }

    private bool HasRelease(Guid extensionId, string releaseHash) =>
        _catalog.GetEntries().Any(entry => entry.ExtensionId == extensionId && entry.ReleaseHash == releaseHash);
}
