using System.Collections.Concurrent;
using System.Diagnostics.Metrics;
using System.Reflection;
using System.Runtime.CompilerServices;
using Agw.Extensions.Contracts;
using Agw.Extensions.Sdk;
using Agw.Extensions.Sdk.Database;
using Agw.Shared.Contracts;
using Agw.Shared.Exceptions;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Agw.Extensions.Infrastructure.Runtime;

public sealed partial class ExtensionRuntime : IExtensionRuntime, IExtensionInvoker, IExtensionGenerationAccessor
{
    private static readonly Meter RuntimeMeter = new("Agw.Extensions.Runtime");
    private static readonly UpDownCounter<long> LoadedGenerations = RuntimeMeter.CreateUpDownCounter<long>(
        "agw.extension.generations.loaded"
    );
    private static readonly UpDownCounter<long> ActiveGenerations = RuntimeMeter.CreateUpDownCounter<long>(
        "agw.extension.generations.active"
    );
    private static readonly UpDownCounter<long> ActiveCalls = RuntimeMeter.CreateUpDownCounter<long>(
        "agw.extension.calls.active"
    );
    private static readonly UpDownCounter<long> ActiveGenerationLeases = RuntimeMeter.CreateUpDownCounter<long>(
        "agw.extension.generation_leases.active"
    );
    private static readonly Counter<long> DrainTimeouts = RuntimeMeter.CreateCounter<long>(
        "agw.extension.drain.timeouts"
    );
    private static readonly Counter<long> UnloadResults = RuntimeMeter.CreateCounter<long>(
        "agw.extension.unload.results"
    );
    private static readonly Counter<long> MigrationResults = RuntimeMeter.CreateCounter<long>(
        "agw.extension.migration.results"
    );
    private static readonly Histogram<double> LifecycleDuration = RuntimeMeter.CreateHistogram<double>(
        "agw.extension.lifecycle.duration",
        "ms"
    );

    private readonly IServiceProvider _hostServices;
    private readonly IExtensionRuntimeAvailability _availability;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<ExtensionRuntime> _logger;
    private readonly ILoggerFactory _loggerFactory;
    private readonly IReadOnlySet<Type> _allowedHostCapabilities;
    private readonly bool _backgroundSchedulingEnabled;
    private readonly ExtensionOptions _options;
    private readonly ConcurrentDictionary<ExtensionGenerationId, ExtensionGeneration> _generations = new();
    private readonly ConcurrentDictionary<Guid, SemaphoreSlim> _extensionLoadLocks = new();

    public ExtensionRuntime(
        IServiceProvider hostServices,
        IExtensionRuntimeAvailability availability,
        TimeProvider timeProvider,
        ILogger<ExtensionRuntime> logger,
        ILoggerFactory loggerFactory
    )
    {
        _hostServices = hostServices;
        _availability = availability;
        _timeProvider = timeProvider;
        _logger = logger;
        _loggerFactory = loggerFactory;
        _options = _hostServices.GetRequiredService<Microsoft.Extensions.Options.IOptions<ExtensionOptions>>().Value;
        _allowedHostCapabilities = _hostServices
            .GetServices<ExtensionHostCapabilityRegistration>()
            .Select(registration => registration.CapabilityType)
            .ToHashSet();
        IExtensionHostIdentity hostIdentity = _hostServices.GetRequiredService<IExtensionHostIdentity>();
        _backgroundSchedulingEnabled = hostIdentity.Role is "ControlPlane" or "Standalone";
        _hostServices.GetRequiredService<ExtensionHostIdentity>().SetUnloadProbe(() => _generations.IsEmpty);
    }

    public async ValueTask<ExtensionGenerationSnapshot> LoadAsync(
        ExtensionRuntimeLoadRequest request,
        CancellationToken cancellationToken = default
    )
    {
        ExtensionRuntimeLoadRequest normalizedRequest = NormalizeRequest(request);
        SemaphoreSlim extensionLoadLock = _extensionLoadLocks.GetOrAdd(
            normalizedRequest.ExtensionId,
            static _ => new SemaphoreSlim(1, 1)
        );
        await extensionLoadLock.WaitAsync(cancellationToken);
        try
        {
            return await LoadGenerationAsync(normalizedRequest, cancellationToken);
        }
        finally
        {
            extensionLoadLock.Release();
        }
    }

    public async ValueTask PrepareDatabaseAsync(
        ExtensionGenerationId generationId,
        CancellationToken cancellationToken = default
    )
    {
        ExtensionGeneration generation = GetGeneration(generationId);
        if (generation.DatabasePrepared)
        {
            return;
        }

        if (generation.HasDatabaseRegistration)
        {
            long started = _timeProvider.GetTimestamp();
            try
            {
                IExtensionDatabase database =
                    _hostServices.GetService<IExtensionDatabase>()
                    ?? throw new AgwException(
                        ErrorCodes.ExtensionMigrationFailed,
                        "Extension database service is unavailable."
                    );
                await database.PrepareAsync(generationId, cancellationToken);
                MigrationResults.Add(1, CreateMetricTags(generation, ("result", "success")));
                LifecycleDuration.Record(
                    _timeProvider.GetElapsedTime(started).TotalMilliseconds,
                    CreateMetricTags(generation, ("operation", "prepare_database"))
                );
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                MigrationResults.Add(1, CreateMetricTags(generation, ("result", "cancelled")));
                LifecycleDuration.Record(
                    _timeProvider.GetElapsedTime(started).TotalMilliseconds,
                    CreateMetricTags(generation, ("operation", "prepare_database"))
                );
                cancellationToken.ThrowIfCancellationRequested();
            }
            catch (Exception exception)
            {
                MigrationResults.Add(1, CreateMetricTags(generation, ("result", "failure")));
                LifecycleDuration.Record(
                    _timeProvider.GetElapsedTime(started).TotalMilliseconds,
                    CreateMetricTags(generation, ("operation", "prepare_database"))
                );
                int? errorCode = exception is AgwException agwException ? agwException.Code : null;
                System.Net.HttpStatusCode? errorStatus = exception is AgwException agwStatusException
                    ? agwStatusException.StatusCode
                    : null;
                string errorMessage = exception.Message;
                string failure = exception.ToString();
                exception = null!;
                _logger.LogError(
                    "Extension {ExtensionId} release {ReleaseHash} generation {GenerationId} database preparation failed: {Failure}",
                    generation.GenerationId.ExtensionId,
                    generation.Request.ReleaseHash,
                    generation.GenerationId.GenerationId,
                    failure
                );
                if (errorCode is { } code && errorStatus is { } status)
                {
                    throw new AgwException(code, errorMessage, status);
                }

                throw new AgwException(ErrorCodes.ExtensionMigrationFailed, errorMessage);
            }
        }

        generation.DatabasePrepared = true;
    }

    public async ValueTask ActivateAsync(
        ExtensionGenerationId generationId,
        CancellationToken cancellationToken = default
    )
    {
        ExtensionGeneration generation = GetGeneration(generationId);
        if (generation.State != ExtensionGenerationState.Prepared || !generation.DatabasePrepared)
        {
            throw new AgwException(ErrorCodes.ExtensionOperationConflict);
        }

        long started = _timeProvider.GetTimestamp();
        try
        {
            await InvokeLifecycleAsync(
                generation,
                async (context, services) =>
                {
                    IServerExtension extension =
                        generation.Extension ?? throw new AgwException(ErrorCodes.ExtensionUnavailable);
                    generation.Started = true;
                    await extension.StartAsync(context, cancellationToken);
                    if (_backgroundSchedulingEnabled)
                    {
                        foreach (Type backgroundServiceType in generation.Registration.BackgroundServiceTypes)
                        {
                            IExtensionBackgroundService backgroundService = (IExtensionBackgroundService)
                                services.GetRequiredService(backgroundServiceType);
                            generation.BackgroundServices.Add(backgroundService);
                            await backgroundService.StartAsync(
                                context,
                                new ExtensionBackgroundScheduler(this, generation.GenerationId),
                                cancellationToken
                            );
                        }
                    }
                },
                cancellationToken
            );
            generation.SetState(ExtensionGenerationState.Ready);
            LifecycleDuration.Record(
                _timeProvider.GetElapsedTime(started).TotalMilliseconds,
                CreateMetricTags(generation, ("operation", "activate"))
            );
            _logger.LogInformation(
                "Extension {ExtensionId} {ExtensionName} release {ReleaseHash} generation {GenerationId} activated",
                generation.GenerationId.ExtensionId,
                generation.Request.Name,
                generation.Request.ReleaseHash,
                generation.GenerationId.GenerationId
            );
        }
        catch (Exception exception)
        {
            generation.DisableBackgroundDispatch();
            generation.SetState(ExtensionGenerationState.Faulted);
            if (generation.ClearActiveMetricCounted())
            {
                ActiveGenerations.Add(-1, CreateMetricTags(generation));
            }
            RethrowSafeException(exception, cancellationToken);

            _logger.LogError(
                "Extension {ExtensionId} release {ReleaseHash} generation {GenerationId} activation failed: {Failure}",
                generation.GenerationId.ExtensionId,
                generation.Request.ReleaseHash,
                generation.GenerationId.GenerationId,
                exception.ToString()
            );
            throw new AgwException(ErrorCodes.ExtensionLoadFailed);
        }
    }

    public async ValueTask<bool> DrainAsync(
        ExtensionGenerationId generationId,
        TimeSpan timeout,
        CancellationToken cancellationToken = default
    )
    {
        ExtensionGeneration generation = GetGeneration(generationId);
        long started = _timeProvider.GetTimestamp();
        using var timeoutCancellation = new CancellationTokenSource(timeout, _timeProvider);
        using var linkedCancellation = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken,
            timeoutCancellation.Token
        );
        try
        {
            await generation.LifecycleGate.WaitAsync(linkedCancellation.Token);
            try
            {
                bool pauseServices = generation.BeginDrain();
                if (pauseServices && generation.ClearActiveMetricCounted())
                {
                    ActiveGenerations.Add(-1, CreateMetricTags(generation));
                }

                if (pauseServices)
                {
                    foreach (IExtensionBackgroundService service in generation.BackgroundServices)
                    {
                        await InvokeLifecycleAsync(
                            generation,
                            (_, _) => service.PauseAsync(linkedCancellation.Token),
                            linkedCancellation.Token
                        );
                    }
                }
            }
            finally
            {
                generation.LifecycleGate.Release();
            }

            TimeSpan remaining = timeout - _timeProvider.GetElapsedTime(started);
            if (remaining <= TimeSpan.Zero)
            {
                RecordDrainTimeout(generation, started);
                return false;
            }

            await generation.WaitForIdleAsync().WaitAsync(remaining, _timeProvider, cancellationToken);
            LifecycleDuration.Record(
                _timeProvider.GetElapsedTime(started).TotalMilliseconds,
                CreateMetricTags(generation, ("operation", "drain"))
            );
            return true;
        }
        catch (TimeoutException)
        {
            RecordDrainTimeout(generation, started);
            return false;
        }
        catch (OperationCanceledException)
            when (timeoutCancellation.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
        {
            RecordDrainTimeout(generation, started);
            return false;
        }
        catch (Exception exception)
        {
            generation.SetState(ExtensionGenerationState.Faulted);
            RethrowSafeException(exception, cancellationToken);

            _logger.LogError(
                "Extension {ExtensionId} release {ReleaseHash} generation {GenerationId} drain failed: {Failure}",
                generation.GenerationId.ExtensionId,
                generation.Request.ReleaseHash,
                generation.GenerationId.GenerationId,
                exception.ToString()
            );
            throw new AgwException(ErrorCodes.ExtensionUnavailable);
        }
    }

    public async ValueTask ResumeAsync(
        ExtensionGenerationId generationId,
        CancellationToken cancellationToken = default
    )
    {
        ExtensionGeneration generation = GetGeneration(generationId);
        await generation.LifecycleGate.WaitAsync(cancellationToken);
        try
        {
            if (generation.State is not (ExtensionGenerationState.Ready or ExtensionGenerationState.Active))
            {
                throw new AgwException(ErrorCodes.ExtensionOperationConflict);
            }

            if (
                generation.State == ExtensionGenerationState.Active
                && (!_backgroundSchedulingEnabled || generation.BackgroundDispatchEnabled)
            )
            {
                return;
            }

            ExtensionExecutionPin pin = CreatePin(generation);
            if (!await _availability.CanAcceptNewWorkAsync(pin, cancellationToken))
            {
                throw new AgwException(ErrorCodes.ExtensionUnavailable);
            }

            try
            {
                ExtensionGenerationState previousState = generation.State;
                generation.SetState(ExtensionGenerationState.Active);
                if (previousState != ExtensionGenerationState.Active && generation.MarkActiveMetricCounted())
                {
                    ActiveGenerations.Add(1, CreateMetricTags(generation));
                }
                if (_backgroundSchedulingEnabled)
                {
                    generation.EnableBackgroundDispatch();
                    foreach (IExtensionBackgroundService service in generation.BackgroundServices)
                    {
                        await InvokeLifecycleAsync(
                            generation,
                            (_, _) => service.ResumeAsync(cancellationToken),
                            cancellationToken
                        );
                    }
                }
            }
            catch (Exception exception)
            {
                generation.DisableBackgroundDispatch();
                generation.SetState(ExtensionGenerationState.Faulted);
                if (generation.ClearActiveMetricCounted())
                {
                    ActiveGenerations.Add(-1, CreateMetricTags(generation));
                }
                RethrowSafeException(exception, cancellationToken);
                _logger.LogError(
                    "Extension {ExtensionId} release {ReleaseHash} generation {GenerationId} resume failed: {Failure}",
                    generation.GenerationId.ExtensionId,
                    generation.Request.ReleaseHash,
                    generation.GenerationId.GenerationId,
                    exception.ToString()
                );
                throw new AgwException(ErrorCodes.ExtensionUnavailable);
            }
        }
        finally
        {
            generation.LifecycleGate.Release();
        }
    }

    public async ValueTask UnloadAsync(
        ExtensionGenerationId generationId,
        TimeSpan unloadConfirmationTimeout,
        CancellationToken cancellationToken = default
    )
    {
        ExtensionGeneration generation = GetGeneration(generationId);
        if (generation.State == ExtensionGenerationState.UnloadFailed)
        {
            long retryStarted = _timeProvider.GetTimestamp();
            if (generation.LoadContextReference is { IsAlive: false })
            {
                _generations.TryRemove(generationId, out _);
                LoadedGenerations.Add(-1, CreateMetricTags(generation));
                UnloadResults.Add(1, CreateMetricTags(generation, ("result", "success")));
                LifecycleDuration.Record(
                    _timeProvider.GetElapsedTime(retryStarted).TotalMilliseconds,
                    CreateMetricTags(generation, ("operation", "unload"))
                );
                return;
            }

            UnloadResults.Add(1, CreateMetricTags(generation, ("result", "failed")));
            LifecycleDuration.Record(
                _timeProvider.GetElapsedTime(retryStarted).TotalMilliseconds,
                CreateMetricTags(generation, ("operation", "unload"))
            );
            throw new AgwException(ErrorCodes.ExtensionUnloadFailed);
        }

        if (!generation.TryBeginStopping())
        {
            throw new AgwException(ErrorCodes.ExtensionOperationConflict);
        }

        long started = _timeProvider.GetTimestamp();
        try
        {
            await StopAndDisposeGenerationAsync(generation, cancellationToken);
            generation.SetState(ExtensionGenerationState.Unloading);
            WeakReference loadContextReference = BeginUnloadContext(generation);
            generation.LoadContextReference = loadContextReference;
            generation.ClearExtensionReferences();

            if (!await ConfirmUnloadAsync(loadContextReference, unloadConfirmationTimeout, cancellationToken))
            {
                generation.SetState(ExtensionGenerationState.UnloadFailed);
                throw new AgwException(ErrorCodes.ExtensionUnloadFailed);
            }

            _generations.TryRemove(generationId, out _);
            LoadedGenerations.Add(-1, CreateMetricTags(generation));
            UnloadResults.Add(1, CreateMetricTags(generation, ("result", "success")));
            LifecycleDuration.Record(
                _timeProvider.GetElapsedTime(started).TotalMilliseconds,
                CreateMetricTags(generation, ("operation", "unload"))
            );
            _logger.LogInformation(
                "Extension {ExtensionId} release {ReleaseHash} generation {GenerationId} unloaded",
                generation.GenerationId.ExtensionId,
                generation.Request.ReleaseHash,
                generation.GenerationId.GenerationId
            );
        }
        catch (Exception exception)
        {
            if (generation.LoadContextReference is not null)
            {
                generation.SetState(ExtensionGenerationState.UnloadFailed);
            }
            else if (generation.State != ExtensionGenerationState.UnloadFailed)
            {
                generation.SetState(ExtensionGenerationState.Faulted);
            }

            RethrowSafeException(exception, cancellationToken);

            _logger.LogError(
                "Extension {ExtensionId} release {ReleaseHash} generation {GenerationId} unload failed: {Failure}",
                generation.GenerationId.ExtensionId,
                generation.Request.ReleaseHash,
                generation.GenerationId.GenerationId,
                exception.ToString()
            );
            UnloadResults.Add(1, CreateMetricTags(generation, ("result", "failed")));
            throw new AgwException(ErrorCodes.ExtensionUnloadFailed);
        }
    }

    public IReadOnlyList<ExtensionGenerationSnapshot> GetSnapshots() =>
        _generations.Values.Select(CreateSnapshot).OrderBy(snapshot => snapshot.GenerationId.ExtensionId).ToArray();

    public async ValueTask<IExtensionGenerationLease> AcquireCurrentAsync(
        Guid extensionId,
        CancellationToken cancellationToken = default
    )
    {
        var candidates = _generations
            .Values.Where(candidate =>
                candidate.GenerationId.ExtensionId == extensionId && candidate.State == ExtensionGenerationState.Active
            )
            .OrderByDescending(candidate => candidate.Request.Epoch)
            .ToArray();

        foreach (var generation in candidates)
        {
            ExtensionExecutionPin pin = CreatePin(generation);
            if (!await _availability.CanAcceptNewWorkAsync(pin, cancellationToken))
            {
                continue;
            }

            if (!generation.TryAcquireLease(allowDraining: false))
            {
                continue;
            }

            try
            {
                if (!await _availability.CanAcceptNewWorkAsync(pin, cancellationToken))
                {
                    generation.ReleaseLease();
                    continue;
                }

                ActiveGenerationLeases.Add(1, CreateMetricTags(generation));
                return new GenerationLease(this, generation, CreateSnapshot(generation), pin);
            }
            catch
            {
                generation.ReleaseLease();
                throw;
            }
        }

        throw new AgwException(ErrorCodes.ExtensionUnavailable);
    }

    public async ValueTask<IExtensionGenerationLease> AcquireForExecutionAsync(
        Guid extensionId,
        CancellationToken cancellationToken = default
    )
    {
        var candidates = _generations
            .Values.Where(candidate =>
                candidate.GenerationId.ExtensionId == extensionId
                && (
                    candidate.State
                    is ExtensionGenerationState.Ready
                        or ExtensionGenerationState.Active
                        or ExtensionGenerationState.Draining
                )
            )
            .OrderByDescending(candidate => candidate.Request.Epoch)
            .ToArray();

        foreach (var generation in candidates)
        {
            if (!generation.TryAcquireLease(allowDraining: true))
            {
                continue;
            }

            ExtensionExecutionPin pin = CreatePin(generation);
            try
            {
                if (!await _availability.CanContinuePinnedWorkAsync(pin, cancellationToken))
                {
                    generation.ReleaseLease();
                    continue;
                }

                ActiveGenerationLeases.Add(1, CreateMetricTags(generation));
                return new GenerationLease(this, generation, CreateSnapshot(generation), pin);
            }
            catch
            {
                generation.ReleaseLease();
                throw;
            }
        }

        throw new AgwException(ErrorCodes.ExtensionUnavailable);
    }

    IExtensionDatabaseGeneration? IExtensionGenerationAccessor.GetDatabaseGeneration(
        ExtensionGenerationId generationId
    ) => _generations.TryGetValue(generationId, out ExtensionGeneration? generation) ? generation.Database : null;

    ExtensionDatabaseGenerationState? IExtensionGenerationAccessor.GetDatabaseGenerationState(
        ExtensionGenerationId generationId
    ) =>
        _generations.TryGetValue(generationId, out ExtensionGeneration? generation)
            ? new ExtensionDatabaseGenerationState(
                generation.Request.ExtensionId,
                generation.Request.Epoch,
                generation.Request.ReleaseHash,
                generation.State
            )
            : null;

    bool IExtensionGenerationAccessor.IsInvocationActive(
        ExtensionGenerationId generationId,
        long epoch,
        Guid invocationId
    ) =>
        _generations.TryGetValue(generationId, out ExtensionGeneration? generation)
        && generation.Request.Epoch == epoch
        && generation.IsInvocationActive(invocationId);

    public async ValueTask StopAsync(CancellationToken cancellationToken = default)
    {
        AgwException? failure = null;
        foreach (ExtensionGeneration generation in _generations.Values.ToArray())
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                if (generation.State is ExtensionGenerationState.Active or ExtensionGenerationState.Draining)
                {
                    bool drained = await DrainAsync(generation.GenerationId, _options.DrainTimeout, cancellationToken);
                    if (!drained)
                    {
                        _logger.LogError(
                            "Extension {ExtensionId} still has active work during shutdown",
                            generation.GenerationId.ExtensionId
                        );
                        failure ??= new AgwException(ErrorCodes.ExtensionDrainTimeout);
                        continue;
                    }
                }

                if (
                    generation.State
                    is ExtensionGenerationState.Prepared
                        or ExtensionGenerationState.Ready
                        or ExtensionGenerationState.Draining
                        or ExtensionGenerationState.Faulted
                        or ExtensionGenerationState.UnloadFailed
                )
                {
                    await UnloadAsync(generation.GenerationId, _options.UnloadTimeout, cancellationToken);
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception exception)
            {
                _logger.LogError(
                    exception,
                    "Extension {ExtensionId} failed to stop during shutdown",
                    generation.GenerationId.ExtensionId
                );
                failure ??= exception is AgwException agwException
                    ? new AgwException(agwException.Code, agwException.Message, agwException.StatusCode)
                    : new AgwException(ErrorCodes.ExtensionUnavailable);
            }
        }

        if (failure is not null)
        {
            throw failure;
        }
    }

    public async ValueTask DisposeAsync()
    {
        int generationCount = Math.Max(1, _generations.Count);
        long perGenerationTicks = _options.DrainTimeout.Ticks + _options.UnloadTimeout.Ticks;
        long shutdownTicks =
            perGenerationTicks > TimeSpan.MaxValue.Ticks / generationCount
                ? TimeSpan.MaxValue.Ticks
                : perGenerationTicks * generationCount;
        using var shutdownTimeout = new CancellationTokenSource(TimeSpan.FromTicks(shutdownTicks), _timeProvider);
        await StopAsync(shutdownTimeout.Token);
    }

    private async ValueTask<ExtensionGenerationSnapshot> LoadGenerationAsync(
        ExtensionRuntimeLoadRequest request,
        CancellationToken cancellationToken
    )
    {
        if (_generations.Values.Any(generation => generation.GenerationId.ExtensionId == request.ExtensionId))
        {
            throw new AgwException(ErrorCodes.ExtensionOperationConflict);
        }

        LoadGenerationResult result = await LoadGenerationCoreAsync(request, cancellationToken);
        if (result.Snapshot is { } snapshot)
        {
            return snapshot;
        }

        await CleanupFailedLoadAsync(result.Generation, result.GenerationCounted);
        if (result.ErrorCode is { } code && result.ErrorStatus is { } status)
        {
            throw new AgwException(code, result.ErrorMessage, status);
        }

        if (result.Canceled && cancellationToken.IsCancellationRequested)
        {
            cancellationToken.ThrowIfCancellationRequested();
        }

        _logger.LogError(
            "Extension {ExtensionId} release {ReleaseHash} load failed: {Failure}",
            request.ExtensionId,
            request.ReleaseHash,
            result.Failure
        );
        throw new AgwException(ErrorCodes.ExtensionLoadFailed);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private async ValueTask<LoadGenerationResult> LoadGenerationCoreAsync(
        ExtensionRuntimeLoadRequest request,
        CancellationToken cancellationToken
    )
    {
        long started = _timeProvider.GetTimestamp();
        ExtensionLoadContext? loadContext = null;
        ExtensionGeneration? generation = null;
        bool generationCounted = false;
        Assembly? assembly = null;
        Type? entryType = null;
        IServerExtension? extension = null;
        IServiceCollection? serviceCollection = null;
        ExtensionRegistration? registration = null;
        ExtensionHostCapabilities? hostCapabilities = null;
        ExtensionUserContext? userContext = null;
        ExtensionDatabaseOptions? databaseOptions = null;
        IExtensionDatabaseFactory? databaseFactory = null;
        ServiceProvider? generationServices = null;
        IExtensionDatabaseGeneration? databaseGeneration = null;
        try
        {
            Directory.CreateDirectory(request.DataDirectory);
            ExtensionGenerationId generationId = new(request.ExtensionId, Guid.CreateVersion7());
            serviceCollection = new ServiceCollection();
            registration = new ExtensionRegistration(serviceCollection);
            hostCapabilities = new ExtensionHostCapabilities(_allowedHostCapabilities);
            ExtensionGenerationSnapshot initialSnapshot = CreatePreparedSnapshot(
                request,
                generationId,
                registration
            ) with
            {
                State = ExtensionGenerationState.Loading,
            };
            generation = new ExtensionGeneration(
                request,
                generationId,
                initialSnapshot,
                loadContext: null,
                extension: null,
                hostCapabilities,
                _loggerFactory.CreateLogger($"Agw.Extensions.{request.Name}"),
                registration,
                userContext: null
            );
            if (!_generations.TryAdd(generationId, generation))
            {
                throw new AgwException(ErrorCodes.ExtensionOperationConflict);
            }

            loadContext = new ExtensionLoadContext(request.EntryAssemblyPath);
            generation.LoadContext = loadContext;
            loadContext = null;
            LoadedGenerations.Add(1, CreateMetricTags(generation));
            generationCounted = true;
            assembly = generation.LoadContext!.LoadFromAssemblyPath(request.EntryAssemblyPath);
            entryType =
                assembly.GetType(request.EntryType, throwOnError: false, ignoreCase: false)
                ?? throw new AgwException(ErrorCodes.ExtensionPackageInvalid, "Extension entry type was not found.");
            if (!typeof(IServerExtension).IsAssignableFrom(entryType) || entryType.IsAbstract)
            {
                throw new AgwException(ErrorCodes.ExtensionPackageInvalid, "Extension entry type is invalid.");
            }

            extension =
                Activator.CreateInstance(entryType) as IServerExtension
                ?? throw new AgwException(ErrorCodes.ExtensionPackageInvalid, "Extension entry could not be created.");
            generation.Extension = extension;
            extension.ConfigureServices(serviceCollection, registration);
            registration.EnforceJsonTypeInfoResolver();
            databaseFactory = registration.DatabaseFactory;
            if (databaseFactory is not null)
            {
                IExtensionDatabaseConnection connection =
                    _hostServices.GetRequiredService<IExtensionDatabaseConnection>();
                userContext = new ExtensionUserContext();
                generation.UserContext = userContext;
                databaseOptions = CreateDatabaseOptions(request, generationId, connection);
                serviceCollection.AddSingleton<IExtensionDatabaseIdentity>(
                    new ExtensionDatabaseIdentity(request.ExtensionId, generationId.GenerationId, request.Epoch)
                );
                serviceCollection.AddSingleton<IExtensionUserContext>(userContext);
                serviceCollection.AddSingleton<IExtensionEpochValidator>(
                    _hostServices.GetRequiredService<IExtensionEpochValidator>()
                );
                serviceCollection.AddSingleton<IExtensionSchemaEpochValidator>(
                    _hostServices.GetRequiredService<IExtensionSchemaEpochValidator>()
                );
                serviceCollection.AddSingleton<IExtensionDatabaseTransactionCoordinator>(
                    _hostServices.GetRequiredService<IExtensionDatabaseTransactionCoordinator>()
                );
                serviceCollection.AddSingleton(_timeProvider);
                databaseFactory.ConfigureServices(serviceCollection, databaseOptions);
            }

            serviceCollection.AddSingleton<IExtensionHostCapabilities>(hostCapabilities);
            if (_backgroundSchedulingEnabled)
            {
                serviceCollection.AddSingleton<IExtensionBackgroundScheduler>(
                    new ExtensionBackgroundScheduler(this, generationId)
                );
            }

            generationServices = serviceCollection.BuildServiceProvider(
                new ServiceProviderOptions { ValidateScopes = true, ValidateOnBuild = false }
            );
            databaseGeneration = databaseFactory?.Create(generationServices, databaseOptions!);
            generation.Services = generationServices;
            generation.Database = databaseGeneration;
            generation.SetSnapshot(CreatePreparedSnapshot(request, generationId, registration));

            assembly = null;
            entryType = null;
            extension = null;
            loadContext = null;
            serviceCollection = null;
            registration = null;
            hostCapabilities = null;
            userContext = null;
            databaseOptions = null;
            databaseFactory = null;
            generationServices = null;
            databaseGeneration = null;
            await ValidateToolRegistrationsAsync(generation, cancellationToken);

            if (generation.HasDatabaseRegistration)
            {
                IExtensionDatabase database =
                    _hostServices.GetService<IExtensionDatabase>()
                    ?? throw new AgwException(
                        ErrorCodes.ExtensionLoadFailed,
                        "Extension database service is unavailable."
                    );
                await database.ValidateModelAsync(generationId, cancellationToken);
            }

            LifecycleDuration.Record(
                _timeProvider.GetElapsedTime(started).TotalMilliseconds,
                CreateMetricTags(generation, ("operation", "load"))
            );
            _logger.LogInformation(
                "Extension {ExtensionId} {ExtensionName} release {ReleaseHash} generation {GenerationId} prepared",
                generationId.ExtensionId,
                request.Name,
                request.ReleaseHash,
                generationId.GenerationId
            );
            return new LoadGenerationResult { Snapshot = CreateSnapshot(generation) };
        }
        catch (Exception exception)
        {
            int? errorCode = exception is AgwException agwException ? agwException.Code : null;
            System.Net.HttpStatusCode? errorStatus = exception is AgwException agwStatusException
                ? agwStatusException.StatusCode
                : null;
            string errorMessage = exception.Message;
            string failure = exception.ToString();
            bool canceled = exception is OperationCanceledException;
            exception = null!;
            assembly = null;
            entryType = null;
            extension = null;
            loadContext = null;
            serviceCollection = null;
            registration = null;
            hostCapabilities = null;
            userContext = null;
            databaseOptions = null;
            databaseFactory = null;
            generationServices = null;
            databaseGeneration = null;
            return new LoadGenerationResult
            {
                Generation = generation,
                GenerationCounted = generationCounted,
                ErrorCode = errorCode,
                ErrorStatus = errorStatus,
                ErrorMessage = errorMessage,
                Failure = failure,
                Canceled = canceled,
            };
        }
    }

    private async ValueTask CleanupFailedLoadAsync(ExtensionGeneration? generation, bool generationCounted)
    {
        if (generation is null)
        {
            return;
        }

        string? cleanupFailure = null;
        try
        {
            if (generation.Database is { } database)
            {
                await database.DisposeAsync();
            }
        }
        catch (Exception exception)
        {
            cleanupFailure = exception.ToString();
            exception = null!;
        }

        try
        {
            if (generation.Services is { } services)
            {
                await services.DisposeAsync();
            }
        }
        catch (Exception exception)
        {
            cleanupFailure ??= exception.ToString();
            exception = null!;
        }

        WeakReference? loadContextReference = null;
        if (generation.LoadContext is not null)
        {
            loadContextReference = BeginUnloadContext(generation);
            generation.LoadContextReference = loadContextReference;
        }
        generation.ClearExtensionReferences();

        bool unloadConfirmed =
            loadContextReference is null
            || await ConfirmUnloadAsync(loadContextReference, _options.UnloadTimeout, CancellationToken.None);
        if (unloadConfirmed)
        {
            _generations.TryRemove(generation.GenerationId, out _);
            if (generationCounted)
            {
                LoadedGenerations.Add(-1, CreateMetricTags(generation));
            }
        }
        else
        {
            generation.SetState(ExtensionGenerationState.UnloadFailed);
            if (!generationCounted)
            {
                LoadedGenerations.Add(1, CreateMetricTags(generation));
            }
        }

        if (cleanupFailure is not null)
        {
            _logger.LogError(
                "Extension {ExtensionId} release {ReleaseHash} generation {GenerationId} failed to dispose load resources: {Failure}",
                generation.GenerationId.ExtensionId,
                generation.Request.ReleaseHash,
                generation.GenerationId.GenerationId,
                cleanupFailure
            );
        }
    }

    private async ValueTask StopAndDisposeGenerationAsync(
        ExtensionGeneration generation,
        CancellationToken cancellationToken
    )
    {
        if (generation.Started && generation.Extension is { } extension)
        {
            await InvokeLifecycleAsync(
                generation,
                async (context, _) =>
                {
                    foreach (
                        IExtensionBackgroundService service in generation.BackgroundServices.AsEnumerable().Reverse()
                    )
                    {
                        await service.StopAsync(cancellationToken);
                    }

                    await extension.StopAsync(context, cancellationToken);
                },
                cancellationToken
            );
        }

        if (generation.Database is { } database)
        {
            await database.DisposeAsync();
        }

        if (generation.Services is { } services)
        {
            await services.DisposeAsync();
        }
    }

    private static async ValueTask ValidateToolRegistrationsAsync(
        ExtensionGeneration generation,
        CancellationToken cancellationToken
    )
    {
        if (generation.Registration.Tools.Count == 0)
        {
            return;
        }

        await using AsyncServiceScope scope = generation.Services!.CreateAsyncScope();
        foreach (RegisteredTool registration in generation.Registration.Tools)
        {
            IExtensionTool? tool = (IExtensionTool)scope.ServiceProvider.GetRequiredService(registration.ToolType);
            if (
                tool.Name != registration.Descriptor.Name
                || tool.RequiredPermission != registration.Descriptor.RequiredPermission
                || tool.AllowInPlanMode != registration.Descriptor.AllowInPlanMode
            )
            {
                throw new AgwException(ErrorCodes.ExtensionPackageInvalid, "Extension tool metadata is invalid.");
            }

            AIFunction? function = tool.CreateFunction(generation.Registration.JsonOptions);
            if (function is null)
            {
                throw new AgwException(ErrorCodes.ExtensionPackageInvalid, "Extension tool function is unavailable.");
            }

            function = null;
            tool = null;
        }
        cancellationToken.ThrowIfCancellationRequested();
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static WeakReference BeginUnloadContext(ExtensionGeneration generation)
    {
        ExtensionLoadContext loadContext =
            generation.LoadContext ?? throw new AgwException(ErrorCodes.ExtensionOperationConflict);
        var weakReference = new WeakReference(loadContext, trackResurrection: false);
        generation.LoadContext = null;
        loadContext.Unload();
        return weakReference;
    }

    private async ValueTask<bool> ConfirmUnloadAsync(
        WeakReference loadContextReference,
        TimeSpan timeout,
        CancellationToken cancellationToken
    )
    {
        long started = _timeProvider.GetTimestamp();
        int attempts = 0;
        long lastCollection = started;
        while (loadContextReference.IsAlive)
        {
            if (attempts < 5 || _timeProvider.GetElapsedTime(lastCollection) >= TimeSpan.FromSeconds(1))
            {
                GC.Collect();
                GC.WaitForPendingFinalizers();
                GC.Collect();
                lastCollection = _timeProvider.GetTimestamp();
            }
            attempts++;
            if (!loadContextReference.IsAlive)
            {
                return true;
            }

            TimeSpan remaining = timeout - _timeProvider.GetElapsedTime(started);
            if (remaining <= TimeSpan.Zero)
            {
                return false;
            }

            await Task.Delay(
                remaining < TimeSpan.FromMilliseconds(Math.Min(1000, 20 * attempts))
                    ? remaining
                    : TimeSpan.FromMilliseconds(Math.Min(1000, 20 * attempts)),
                _timeProvider,
                cancellationToken
            );
        }

        return true;
    }

    private async ValueTask InvokeLifecycleAsync(
        ExtensionGeneration generation,
        Func<IExtensionContext, IServiceProvider, ValueTask> action,
        CancellationToken cancellationToken
    )
    {
        await using AsyncServiceScope scope = _hostServices.CreateAsyncScope();
        ICurrentUser currentUser = scope.ServiceProvider.GetRequiredService<ICurrentUser>();
        Guid invocationId = generation.StartLifecycleInvocation();
        IDisposable? capabilitiesScope = null;
        IDisposable? userContextScope = null;
        try
        {
            capabilitiesScope = generation.HostCapabilities.Push(scope.ServiceProvider);
            userContextScope = generation.UserContext?.Push(currentUser.UserId, invocationId);
            var context = new ExtensionContext(
                generation.Request,
                generation.GenerationId,
                generation.HostCapabilities,
                currentUser,
                generation.Registration.JsonOptions,
                generation.Logger
            );
            await action(context, generation.Services!);
        }
        finally
        {
            userContextScope?.Dispose();
            capabilitiesScope?.Dispose();
            generation.FinishInvocation(invocationId);
        }
    }

    private static ExtensionRuntimeLoadRequest NormalizeRequest(ExtensionRuntimeLoadRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (
            request.ExtensionId == Guid.Empty
            || string.IsNullOrWhiteSpace(request.Name)
            || string.IsNullOrWhiteSpace(request.ReleaseHash)
            || string.IsNullOrWhiteSpace(request.PackageDirectory)
            || string.IsNullOrWhiteSpace(request.EntryAssemblyPath)
            || string.IsNullOrWhiteSpace(request.EntryType)
            || string.IsNullOrWhiteSpace(request.DataDirectory)
        )
        {
            throw new AgwException(ErrorCodes.ExtensionPackageInvalid);
        }

        string packageDirectory = Path.GetFullPath(request.PackageDirectory);
        string entryAssemblyPath = Path.GetFullPath(
            Path.IsPathRooted(request.EntryAssemblyPath)
                ? request.EntryAssemblyPath
                : Path.Combine(packageDirectory, request.EntryAssemblyPath)
        );
        string relativeAssemblyPath = Path.GetRelativePath(packageDirectory, entryAssemblyPath);
        if (
            relativeAssemblyPath == ".."
            || relativeAssemblyPath.StartsWith($"..{Path.DirectorySeparatorChar}", StringComparison.Ordinal)
            || Path.IsPathRooted(relativeAssemblyPath)
        )
        {
            throw new AgwException(ErrorCodes.ExtensionPackageInvalid);
        }

        return request with
        {
            PackageDirectory = packageDirectory,
            EntryAssemblyPath = entryAssemblyPath,
            DataDirectory = Path.GetFullPath(request.DataDirectory),
        };
    }

    private static ExtensionDatabaseOptions CreateDatabaseOptions(
        ExtensionRuntimeLoadRequest request,
        ExtensionGenerationId generationId,
        IExtensionDatabaseConnection connection
    ) =>
        new()
        {
            ExtensionId = request.ExtensionId,
            GenerationId = generationId.GenerationId,
            Name = request.Name,
            Version = request.Version,
            ReleaseHash = request.ReleaseHash,
            Epoch = request.Epoch,
            TablePrefix = $"zext_{request.ExtensionId:N}_",
            Provider = connection.Provider,
            ConnectionString = connection.ConnectionString,
        };

    private static ExtensionGenerationSnapshot CreatePreparedSnapshot(
        ExtensionRuntimeLoadRequest request,
        ExtensionGenerationId generationId,
        ExtensionRegistration registration
    ) =>
        new()
        {
            GenerationId = generationId,
            Name = request.Name,
            Version = request.Version,
            ReleaseHash = request.ReleaseHash,
            Epoch = request.Epoch,
            State = ExtensionGenerationState.Prepared,
            Endpoints = registration.Endpoints.Select(endpoint => endpoint.Descriptor).ToArray(),
            Tools = registration.Tools.Select(tool => tool.Descriptor).ToArray(),
            Skills = registration.Skills.Select(skill => skill.Descriptor).ToArray(),
        };

    private static ExtensionGenerationSnapshot CreateSnapshot(ExtensionGeneration generation)
    {
        (int activeInvocations, int activeLeases) = generation.GetActivityCounts();
        return generation.Snapshot with
        {
            ActiveInvocations = activeInvocations,
            ActiveLeases = activeLeases,
            Endpoints = generation
                .Snapshot.Endpoints.Select(endpoint =>
                    endpoint with
                    {
                        RequestSchema = endpoint.RequestSchema?.Clone(),
                        ResponseSchema = endpoint.ResponseSchema?.Clone(),
                    }
                )
                .ToArray(),
            Tools = generation
                .Snapshot.Tools.Select(tool => tool with { InputSchema = tool.InputSchema.Clone() })
                .ToArray(),
        };
    }

    private static ExtensionExecutionPin CreatePin(ExtensionGeneration generation) =>
        new(generation.GenerationId.ExtensionId, generation.Request.ReleaseHash, generation.Request.Epoch);

    private static void RethrowSafeException(Exception exception, CancellationToken cancellationToken)
    {
        if (exception is AgwException agwException)
        {
            throw new AgwException(agwException.Code, agwException.Message, agwException.StatusCode);
        }

        if (exception is OperationCanceledException)
        {
            cancellationToken.ThrowIfCancellationRequested();
        }
    }

    private static KeyValuePair<string, object?>[] CreateMetricTags(
        ExtensionGeneration generation,
        params (string Name, string Value)[] extraTags
    )
    {
        var tags = new List<KeyValuePair<string, object?>>
        {
            new("extension.id", generation.GenerationId.ExtensionId.ToString("D")),
            new("extension.name", generation.Request.Name),
            new("extension.release_hash", generation.Request.ReleaseHash),
            new("extension.generation_id", generation.GenerationId.GenerationId.ToString("D")),
        };
        tags.AddRange(extraTags.Select(tag => new KeyValuePair<string, object?>(tag.Name, tag.Value)));
        return tags.ToArray();
    }

    private void RecordDrainTimeout(ExtensionGeneration generation, long started)
    {
        DrainTimeouts.Add(1, CreateMetricTags(generation));
        LifecycleDuration.Record(
            _timeProvider.GetElapsedTime(started).TotalMilliseconds,
            CreateMetricTags(generation, ("operation", "drain"))
        );
        _logger.LogInformation(
            "Extension {ExtensionId} release {ReleaseHash} generation {GenerationId} drain timed out",
            generation.GenerationId.ExtensionId,
            generation.Request.ReleaseHash,
            generation.GenerationId.GenerationId
        );
    }

    private sealed record LoadGenerationResult
    {
        public ExtensionGenerationSnapshot? Snapshot { get; init; }

        public ExtensionGeneration? Generation { get; init; }

        public bool GenerationCounted { get; init; }

        public int? ErrorCode { get; init; }

        public System.Net.HttpStatusCode? ErrorStatus { get; init; }

        public string ErrorMessage { get; init; } = string.Empty;

        public string Failure { get; init; } = string.Empty;

        public bool Canceled { get; init; }
    }

    private ExtensionGeneration GetGeneration(ExtensionGenerationId generationId) =>
        _generations.TryGetValue(generationId, out ExtensionGeneration? generation)
            ? generation
            : throw new AgwException(ErrorCodes.ExtensionUnavailable);
}
