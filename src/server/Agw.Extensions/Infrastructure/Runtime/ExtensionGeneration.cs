using Agw.Extensions.Contracts;
using Agw.Extensions.Sdk;
using Agw.Extensions.Sdk.Database;
using Agw.Shared.Exceptions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Agw.Extensions.Infrastructure.Runtime;

internal sealed class ExtensionGeneration
{
    private readonly object _gate = new();
    private readonly HashSet<Guid> _activeInvocationIds = [];
    private TaskCompletionSource _idle = CreateCompletedSource();
    private readonly SemaphoreSlim _lifecycleGate = new(1, 1);
    private int _activeInvocations;
    private int _activeLeases;
    private bool _backgroundDispatchEnabled;
    private bool _activeMetricCounted;

    public ExtensionGeneration(
        ExtensionRuntimeLoadRequest request,
        ExtensionGenerationId generationId,
        ExtensionGenerationSnapshot snapshot,
        ExtensionLoadContext? loadContext,
        IServerExtension? extension,
        ExtensionHostCapabilities hostCapabilities,
        ILogger logger,
        ExtensionRegistration registration,
        ExtensionUserContext? userContext
    )
    {
        Request = request;
        GenerationId = generationId;
        Snapshot = snapshot;
        LoadContext = loadContext;
        Extension = extension;
        HostCapabilities = hostCapabilities;
        Logger = logger;
        Registration = registration;
        UserContext = userContext;
    }

    public ExtensionRuntimeLoadRequest Request { get; }

    public ExtensionGenerationId GenerationId { get; }

    public ExtensionGenerationSnapshot Snapshot { get; private set; }

    public ExtensionLoadContext? LoadContext { get; set; }

    public IServerExtension? Extension { get; set; }

    public ExtensionHostCapabilities HostCapabilities { get; }

    public ILogger Logger { get; }

    public ExtensionRegistration Registration { get; }

    public ExtensionUserContext? UserContext { get; set; }

    public ServiceProvider? Services { get; set; }

    public IExtensionDatabaseGeneration? Database { get; set; }

    public bool DatabasePrepared { get; set; }

    public SemaphoreSlim LifecycleGate => _lifecycleGate;

    public bool Started { get; set; }

    public WeakReference? LoadContextReference { get; set; }

    public bool BackgroundDispatchEnabled
    {
        get
        {
            lock (_gate)
            {
                return _backgroundDispatchEnabled;
            }
        }
    }

    public List<IExtensionBackgroundService> BackgroundServices { get; } = [];

    public ExtensionGenerationState State
    {
        get
        {
            lock (_gate)
            {
                return Snapshot.State;
            }
        }
    }

    public bool HasDatabaseRegistration => Registration.DatabaseFactory is not null;

    public bool TryAcquireLease(bool allowDraining)
    {
        lock (_gate)
        {
            if (
                Snapshot.State != ExtensionGenerationState.Active
                && !(
                    allowDraining
                    && (Snapshot.State is ExtensionGenerationState.Ready or ExtensionGenerationState.Draining)
                )
            )
            {
                return false;
            }

            StartTrackingActivity();
            _activeLeases++;
            return true;
        }
    }

    public void ReleaseLease()
    {
        lock (_gate)
        {
            _activeLeases--;
            StopTrackingActivity();
        }
    }

    public bool TryStartInvocation(bool allowDraining, bool backgroundWork, out Guid invocationId)
    {
        lock (_gate)
        {
            bool active = Snapshot.State == ExtensionGenerationState.Active;
            bool draining =
                allowDraining
                && (Snapshot.State is ExtensionGenerationState.Ready or ExtensionGenerationState.Draining)
                && _activeLeases > 0;
            if ((!active && !draining) || (backgroundWork && !_backgroundDispatchEnabled))
            {
                invocationId = default;
                return false;
            }

            invocationId = StartInvocationTracking();
            return true;
        }
    }

    public Guid StartLifecycleInvocation()
    {
        lock (_gate)
        {
            return StartInvocationTracking();
        }
    }

    public bool IsInvocationActive(Guid invocationId)
    {
        lock (_gate)
        {
            return _activeInvocationIds.Contains(invocationId);
        }
    }

    public void FinishInvocation(Guid invocationId)
    {
        lock (_gate)
        {
            if (_activeInvocationIds.Remove(invocationId))
            {
                _activeInvocations--;
                StopTrackingActivity();
            }
        }
    }

    public bool BeginDrain()
    {
        lock (_gate)
        {
            if (Snapshot.State == ExtensionGenerationState.Draining)
            {
                return false;
            }

            if (Snapshot.State is not (ExtensionGenerationState.Active or ExtensionGenerationState.Ready))
            {
                throw new AgwException(ErrorCodes.ExtensionOperationConflict);
            }

            _backgroundDispatchEnabled = false;
            SetState(ExtensionGenerationState.Draining);
            return true;
        }
    }

    public void SetState(ExtensionGenerationState state)
    {
        lock (_gate)
        {
            Snapshot = Snapshot with { State = state };
        }
    }

    public void SetSnapshot(ExtensionGenerationSnapshot snapshot)
    {
        lock (_gate)
        {
            Snapshot = snapshot;
        }
    }

    public void EnableBackgroundDispatch()
    {
        lock (_gate)
        {
            _backgroundDispatchEnabled = true;
        }
    }

    public void DisableBackgroundDispatch()
    {
        lock (_gate)
        {
            _backgroundDispatchEnabled = false;
        }
    }

    public bool MarkActiveMetricCounted()
    {
        lock (_gate)
        {
            if (_activeMetricCounted)
            {
                return false;
            }

            _activeMetricCounted = true;
            return true;
        }
    }

    public bool ClearActiveMetricCounted()
    {
        lock (_gate)
        {
            if (!_activeMetricCounted)
            {
                return false;
            }

            _activeMetricCounted = false;
            return true;
        }
    }

    public Task WaitForIdleAsync()
    {
        lock (_gate)
        {
            return _idle.Task;
        }
    }

    public bool TryBeginStopping()
    {
        lock (_gate)
        {
            if (
                Snapshot.State
                    is not (
                        ExtensionGenerationState.Prepared
                        or ExtensionGenerationState.Ready
                        or ExtensionGenerationState.Draining
                        or ExtensionGenerationState.Faulted
                    )
                || _activeInvocations != 0
                || _activeLeases != 0
            )
            {
                return false;
            }

            _backgroundDispatchEnabled = false;
            Snapshot = Snapshot with { State = ExtensionGenerationState.Stopping };
            return true;
        }
    }

    public (int ActiveInvocations, int ActiveLeases) GetActivityCounts()
    {
        lock (_gate)
        {
            return (_activeInvocations, _activeLeases);
        }
    }

    public void ClearExtensionReferences()
    {
        UserContext?.Clear();
        HostCapabilities.Clear();
        Extension = null;
        Services = null;
        Database = null;
        Started = false;
        UserContext = null;
        BackgroundServices.Clear();
        Registration.Clear();
        LoadContext = null;
    }

    private void StartTrackingActivity()
    {
        if (_activeInvocations == 0 && _activeLeases == 0)
        {
            _idle = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        }
    }

    private Guid StartInvocationTracking()
    {
        StartTrackingActivity();
        _activeInvocations++;
        Guid invocationId = Guid.CreateVersion7();
        _activeInvocationIds.Add(invocationId);
        return invocationId;
    }

    private void StopTrackingActivity()
    {
        if (_activeInvocations == 0 && _activeLeases == 0)
        {
            _idle.TrySetResult();
        }
    }

    private static TaskCompletionSource CreateCompletedSource()
    {
        var source = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        source.SetResult();
        return source;
    }
}
