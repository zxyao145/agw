using System.Collections.Concurrent;
using System.Threading.Channels;
using Agw.Agents.Contracts.Execution;
using Agw.Extensions.Application.Packages;
using Agw.Extensions.Application.Persistence;
using Agw.Extensions.Application.Runtime;
using Agw.Extensions.Contracts;
using Agw.Extensions.Infrastructure.Packages;
using Agw.Shared.Exceptions;
using Agw.Shared.Runtime;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Agw.Extensions.Infrastructure.Runtime;

public sealed class ExtensionNodeWorker : BackgroundService, IExtensionNodeReconciler
{
    private readonly IExtensionRuntime _runtime;
    private readonly IExtensionEnablementStore _enablement;
    private readonly IServiceScopeFactory _scopes;
    private readonly ExtensionPackageCatalog _catalog;
    private readonly ExtensionEndpointDataSource _endpoints;
    private readonly ExtensionHostIdentity _host;
    private readonly ExtensionOptions _options;
    private readonly TimeProvider _time;
    private readonly IServerInitializationState _initialization;
    private readonly AgwDataPaths _paths;
    private readonly ILogger<ExtensionNodeWorker> _logger;
    private readonly Channel<Guid> _requests = Channel.CreateUnbounded<Guid>();
    private readonly ConcurrentDictionary<Guid, SemaphoreSlim> _locks = new();
    private readonly ConcurrentDictionary<Guid, FailureState> _errors = new();
    private readonly ConcurrentDictionary<Guid, Task> _tasks = new();

    public ExtensionNodeWorker(
        IExtensionRuntime runtime,
        IExtensionEnablementStore enablement,
        IServiceScopeFactory scopes,
        ExtensionPackageCatalog catalog,
        ExtensionEndpointDataSource endpoints,
        ExtensionHostIdentity host,
        IOptions<ExtensionOptions> options,
        TimeProvider time,
        IServerInitializationState initialization,
        AgwDataPaths paths,
        ILogger<ExtensionNodeWorker> logger
    )
    {
        _runtime = runtime;
        _enablement = enablement;
        _scopes = scopes;
        _catalog = catalog;
        _endpoints = endpoints;
        _host = host;
        _options = options.Value;
        _time = time;
        _initialization = initialization;
        _paths = paths;
        _logger = logger;
    }

    public override async Task StartAsync(CancellationToken cancellationToken)
    {
        await _catalog.InitializeAsync(cancellationToken);
        if (_initialization.IsInitialized)
            await ReconcileAllAsync(cancellationToken);
        await base.StartAsync(cancellationToken);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(_options.StatePollInterval, _time);
        var requestWait = _requests.Reader.WaitToReadAsync(stoppingToken).AsTask();
        var timerWait = timer.WaitForNextTickAsync(stoppingToken).AsTask();
        while (!stoppingToken.IsCancellationRequested)
        {
            await Task.WhenAny(requestWait, timerWait);
            if (requestWait.IsCompleted)
            {
                if (!await requestWait)
                    return;
                requestWait = _requests.Reader.WaitToReadAsync(stoppingToken).AsTask();
                var ids = new HashSet<Guid>();
                while (_requests.Reader.TryRead(out var extensionId))
                    ids.Add(extensionId);
                if (_initialization.IsInitialized)
                    foreach (var extensionId in ids)
                        _ = ScheduleReconciliation(extensionId, stoppingToken);
            }
            if (timerWait.IsCompleted)
            {
                if (!await timerWait)
                    return;
                timerWait = timer.WaitForNextTickAsync(stoppingToken).AsTask();
                if (_initialization.IsInitialized)
                {
                    ScheduleAll(stoppingToken);
                }
            }
        }
    }

    public void RequestReconciliation(Guid extensionId)
    {
        if (!_requests.Writer.TryWrite(extensionId))
            throw new AgwException(ErrorCodes.ExtensionUnavailable, "Extension runtime reconciliation is unavailable.");
    }

    public ExtensionRuntimeError? GetError(Guid extensionId) =>
        _errors.TryGetValue(extensionId, out var failure)
            ? new ExtensionRuntimeError(failure.Code, failure.Message)
            : null;

    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        _requests.Writer.TryComplete();
        var tasks = _tasks.Values.ToArray();
        AgwException? failure = null;
        try
        {
            await base.StopAsync(cancellationToken);
            await Task.WhenAll(tasks);
        }
        catch (OperationCanceledException)
            when (cancellationToken.IsCancellationRequested
                || tasks.All(task => task.IsCanceled || task.IsCompletedSuccessfully)
            )
        {
            _logger.LogDebug("Extension reconciliations were canceled during shutdown");
        }
        catch (Exception exception)
        {
            _logger.LogError(exception, "Extension reconciliations failed during shutdown");
            failure = exception is AgwException agwException
                ? new AgwException(agwException.Code, agwException.Message, agwException.StatusCode)
                : new AgwException(ErrorCodes.ExtensionUnavailable);
        }

        try
        {
            await _runtime.StopAsync(cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            _logger.LogDebug("Extension runtime shutdown was canceled");
        }
        catch (Exception exception)
        {
            _logger.LogError(exception, "Extension runtime failed during shutdown");
            failure ??= exception is AgwException agwException
                ? new AgwException(agwException.Code, agwException.Message, agwException.StatusCode)
                : new AgwException(ErrorCodes.ExtensionUnavailable);
        }
        finally
        {
            _endpoints.Publish(() => []);
        }

        cancellationToken.ThrowIfCancellationRequested();
        if (failure is not null)
        {
            throw failure;
        }
    }

    private async Task ReconcileAllAsync(CancellationToken cancellationToken)
    {
        await Task.WhenAll(_catalog.GetEntries().Select(entry => ReconcileAsync(entry.ExtensionId, cancellationToken)));
    }

    private void ScheduleAll(CancellationToken cancellationToken)
    {
        foreach (var extensionId in _catalog.GetEntries().Select(entry => entry.ExtensionId))
            _ = ScheduleReconciliation(extensionId, cancellationToken);
    }

    private Task ScheduleReconciliation(Guid extensionId, CancellationToken cancellationToken)
    {
        while (true)
        {
            if (_tasks.TryGetValue(extensionId, out var current))
            {
                if (!current.IsCompleted)
                    return current;
                _tasks.TryRemove(extensionId, out _);
                continue;
            }

            var completion = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            if (!_tasks.TryAdd(extensionId, completion.Task))
                continue;
            _ = RunReconciliationAsync(extensionId, cancellationToken, completion);
            return completion.Task;
        }
    }

    private async Task RunReconciliationAsync(
        Guid extensionId,
        CancellationToken cancellationToken,
        TaskCompletionSource<bool> completion
    )
    {
        try
        {
            await ReconcileAsync(extensionId, cancellationToken);
            completion.TrySetResult(true);
        }
        catch (OperationCanceledException exception)
        {
            completion.TrySetCanceled(exception.CancellationToken);
        }
        catch (Exception exception)
        {
            _logger.LogError(exception, "Extension {ExtensionId} reconciliation task failed", extensionId);
            completion.TrySetResult(false);
        }
        finally
        {
            ((ICollection<KeyValuePair<Guid, Task>>)_tasks).Remove(new(extensionId, completion.Task));
        }
    }

    private async Task ReconcileAsync(Guid extensionId, CancellationToken cancellationToken)
    {
        var extensionLock = _locks.GetOrAdd(extensionId, static _ => new SemaphoreSlim(1, 1));
        await extensionLock.WaitAsync(cancellationToken);
        ExtensionEnablementState? observedEnablement = null;
        ExtensionLocalPackage? observedEntry = null;
        try
        {
            var enablement = await _enablement.GetAsync(extensionId, cancellationToken);
            observedEnablement = enablement;
            var entry = _catalog.GetValidPackage(extensionId);
            observedEntry = entry;
            var current = _runtime
                .GetSnapshots()
                .SingleOrDefault(snapshot => snapshot.GenerationId.ExtensionId == extensionId);

            var releaseHash = entry.ReleaseHash;
            if (
                _errors.TryGetValue(extensionId, out var previousFailure)
                && enablement.Enabled
                && previousFailure.Code != ErrorCodes.ExtensionDrainTimeout.Code
                && previousFailure.Code != ErrorCodes.ExtensionUnloadFailed.Code
                && previousFailure.Code != ErrorCodes.ExtensionOperationConflict.Code
                && previousFailure.SettingsVersion == enablement.Version
                && previousFailure.ReleaseHash == releaseHash
            )
                return;

            if (!enablement.Enabled)
            {
                if (current is not null)
                {
                    await StopGenerationAsync(
                        extensionId,
                        current,
                        waitForContinuations: true,
                        disabledSettingsVersion: enablement.Version,
                        cancellationToken: cancellationToken
                    );
                }
                else if (await HasNonterminalExecutionsAsync(extensionId, cancellationToken))
                {
                    await StartGenerationAsync(entry, enablement, resume: false, cancellationToken: cancellationToken);
                }
                _errors.TryRemove(extensionId, out _);
                return;
            }

            if (
                current is not null
                && current.State == ExtensionGenerationState.Active
                && current.Epoch == enablement.Version
                && current.ReleaseHash == entry.ReleaseHash
            )
            {
                _errors.TryRemove(extensionId, out _);
                return;
            }

            if (current is not null)
                await StopGenerationAsync(extensionId, current, waitForContinuations: false, cancellationToken);

            await StartGenerationAsync(entry, enablement, resume: true, cancellationToken: cancellationToken);
            _errors.TryRemove(extensionId, out _);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            if (observedEnablement is not { } state)
                throw;
            var failure = new FailureState(
                state.Version,
                observedEntry?.ReleaseHash,
                exception is AgwException agwException ? agwException.Code : ErrorCodes.ExtensionLoadFailed.Code,
                exception.Message
            );
            _errors[extensionId] = failure;
            _logger.LogError(
                exception,
                "Extension {ExtensionId} on Host {HostId} failed to reconcile at settings version {SettingsVersion}",
                extensionId,
                _host.HostId,
                state.Version
            );
        }
        finally
        {
            _endpoints.Publish(_runtime.GetSnapshots);
            extensionLock.Release();
        }
    }

    private async Task StartGenerationAsync(
        ExtensionLocalPackage package,
        ExtensionEnablementState enablement,
        bool resume,
        CancellationToken cancellationToken
    )
    {
        var manifest = package.Manifest;
        var dataDirectory = Path.Combine(_paths.Root, "extensions", "data", package.ExtensionId.ToString("N"));
        Directory.CreateDirectory(dataDirectory);
        var snapshot = await _runtime.LoadAsync(
            new ExtensionRuntimeLoadRequest
            {
                ExtensionId = package.ExtensionId,
                Name = manifest.Name,
                Version = manifest.Version,
                ReleaseHash = package.ReleaseHash,
                Epoch = enablement.Version,
                PackageDirectory = package.SnapshotDirectory,
                DataDirectory = dataDirectory,
                EntryAssemblyPath = manifest.Server!.EntryAssembly,
                EntryType = manifest.Server.EntryType,
                EventPermissions = manifest.Server.Permissions.Events,
            },
            cancellationToken
        );
        await _runtime.PrepareDatabaseAsync(snapshot.GenerationId, cancellationToken);

        var currentEnablement = await _enablement.GetAsync(package.ExtensionId, cancellationToken);
        if (currentEnablement.Enabled != enablement.Enabled || currentEnablement.Version != enablement.Version)
        {
            await _runtime.UnloadAsync(snapshot.GenerationId, _options.UnloadTimeout, cancellationToken);
            return;
        }

        await _runtime.ActivateAsync(snapshot.GenerationId, cancellationToken);
        currentEnablement = await _enablement.GetAsync(package.ExtensionId, cancellationToken);
        if (currentEnablement.Enabled != enablement.Enabled || currentEnablement.Version != enablement.Version)
        {
            await _runtime.UnloadAsync(snapshot.GenerationId, _options.UnloadTimeout, cancellationToken);
            return;
        }
        if (resume)
        {
            // 在 generation 进入 Active 之前发布 Ready 路由。
            // Publish the Ready routes before the generation becomes Active.
            _endpoints.Publish(_runtime.GetSnapshots);
            await _runtime.ResumeAsync(snapshot.GenerationId, cancellationToken);
        }
    }

    private async Task StopGenerationAsync(
        Guid extensionId,
        ExtensionGenerationSnapshot snapshot,
        bool waitForContinuations,
        CancellationToken cancellationToken,
        long? disabledSettingsVersion = null
    )
    {
        var deadline = _time.GetUtcNow() + _options.DrainTimeout;
        if (
            snapshot.State
                is (
                    ExtensionGenerationState.Active
                    or ExtensionGenerationState.Ready
                    or ExtensionGenerationState.Draining
                )
            && !await _runtime.DrainAsync(snapshot.GenerationId, _options.DrainTimeout, cancellationToken)
        )
            throw new AgwException(ErrorCodes.ExtensionDrainTimeout);

        while (waitForContinuations && await HasNonterminalExecutionsAsync(extensionId, cancellationToken))
        {
            if (disabledSettingsVersion is { } settingsVersion)
            {
                ExtensionEnablementState currentEnablement = await _enablement.GetAsync(extensionId, cancellationToken);
                if (currentEnablement.Enabled || currentEnablement.Version != settingsVersion)
                {
                    break;
                }
            }

            var remaining = deadline - _time.GetUtcNow();
            if (remaining <= TimeSpan.Zero)
                throw new AgwException(ErrorCodes.ExtensionDrainTimeout);
            await Task.Delay(
                TimeSpan.FromMilliseconds(
                    Math.Min(_options.StatePollInterval.TotalMilliseconds, remaining.TotalMilliseconds)
                ),
                _time,
                cancellationToken
            );
        }

        await _runtime.UnloadAsync(snapshot.GenerationId, _options.UnloadTimeout, cancellationToken);
    }

    private async Task<bool> HasNonterminalExecutionsAsync(Guid extensionId, CancellationToken cancellationToken)
    {
        await using var scope = _scopes.CreateAsyncScope();
        return await scope
            .ServiceProvider.GetRequiredService<IExtensionExecutionUsage>()
            .HasNonterminalExecutionsAsync(extensionId, cancellationToken);
    }

    private sealed record FailureState(long SettingsVersion, string? ReleaseHash, int Code, string Message);
}
