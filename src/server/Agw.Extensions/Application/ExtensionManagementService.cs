using Agw.Extensions.Application.Packages;
using Agw.Extensions.Application.Persistence;
using Agw.Extensions.Application.Runtime;
using Agw.Extensions.Contracts;
using Agw.Shared;
using Agw.Shared.Contracts;
using Agw.Shared.Exceptions;

namespace Agw.Extensions.Application;

public sealed class ExtensionManagementService
{
    private readonly ICurrentUser _user;
    private readonly IExtensionEnablementStore _enablement;
    private readonly IExtensionPackageCatalog _catalog;
    private readonly IExtensionNodeReconciler _worker;
    private readonly IExtensionRuntime _runtime;

    public ExtensionManagementService(
        ICurrentUser user,
        IExtensionEnablementStore enablement,
        IExtensionPackageCatalog catalog,
        IExtensionNodeReconciler worker,
        IExtensionRuntime runtime
    )
    {
        _user = user;
        _enablement = enablement;
        _catalog = catalog;
        _worker = worker;
        _runtime = runtime;
    }

    public async Task<IReadOnlyList<ExtensionManagementStatus>> ListAsync(CancellationToken cancellationToken)
    {
        RequireAdministrator();
        var statuses = new List<ExtensionManagementStatus>();
        foreach (var entry in _catalog.GetEntries().OrderBy(entry => entry.Manifest!.Name, StringComparer.Ordinal))
        {
            var extensionId = entry.ExtensionId;
            var enablement = await _enablement.GetAsync(extensionId, cancellationToken);
            statuses.Add(CreateStatus(entry, enablement));
        }
        return statuses;
    }

    public async Task<ExtensionManagementStatus> SetEnabledAsync(
        ExtensionEnablementRequest request,
        bool enabled,
        CancellationToken cancellationToken
    )
    {
        RequireAdministrator();
        if (request.ExtensionId == Guid.Empty)
            throw new AgwException(ErrorCodes.InvalidParam);

        var entry = _catalog.GetEntries().SingleOrDefault(entry => entry.ExtensionId == request.ExtensionId);
        if (entry is null)
            throw new AgwException(ErrorCodes.ExtensionNotFound);

        var enablement = await _enablement.SetAsync(request.ExtensionId, enabled, cancellationToken);
        _worker.RequestReconciliation(request.ExtensionId);
        return CreateStatus(entry, enablement);
    }

    private ExtensionManagementStatus CreateStatus(ExtensionLocalPackage entry, ExtensionEnablementState enablement)
    {
        var extensionId = entry.ExtensionId;
        var snapshot = _runtime.GetSnapshots().SingleOrDefault(item => item.GenerationId.ExtensionId == extensionId);
        var runtimeError = _worker.GetError(extensionId);
        var errorCode = runtimeError?.Code;
        var errorMessage = runtimeError?.Message;
        var state =
            runtimeError is not null ? ExtensionManagementState.Failed
            : snapshot is not null
                ? snapshot.State switch
                {
                    ExtensionGenerationState.Loading => ExtensionManagementState.Loading,
                    ExtensionGenerationState.Prepared => ExtensionManagementState.Prepared,
                    ExtensionGenerationState.Ready => ExtensionManagementState.Ready,
                    ExtensionGenerationState.Active => ExtensionManagementState.Active,
                    ExtensionGenerationState.Draining => ExtensionManagementState.Draining,
                    ExtensionGenerationState.Stopping => ExtensionManagementState.Stopping,
                    ExtensionGenerationState.Unloading => ExtensionManagementState.Unloading,
                    ExtensionGenerationState.Faulted => ExtensionManagementState.Faulted,
                    ExtensionGenerationState.UnloadFailed => ExtensionManagementState.UnloadFailed,
                    _ => throw new AgwException(ErrorCodes.ExtensionUnavailable),
                }
            : enablement.Enabled ? ExtensionManagementState.Starting
            : ExtensionManagementState.Disabled;

        return new ExtensionManagementStatus
        {
            Id = extensionId,
            Name = entry.Manifest!.Name,
            DisplayName = entry.Manifest.DisplayName,
            Targets = entry.Manifest.Targets,
            CurrentVersion = entry.Manifest.Version,
            IsEnabled = enablement.Enabled,
            SettingsVersion = enablement.Version,
            State = state,
            ErrorCode = errorCode,
            ErrorMessage = errorMessage,
            ActiveInvocations = snapshot?.ActiveInvocations ?? 0,
            ActiveLeases = snapshot?.ActiveLeases ?? 0,
        };
    }

    private void RequireAdministrator()
    {
        if (_user.RequiredUserId != Constants.AdminUserId)
            throw new AgwException(ErrorCodes.AdministratorRequired);
    }
}
