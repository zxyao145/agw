using System.Collections.Concurrent;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Threading.Channels;
using A2A;
using Agw.A2A.Extensions;
using Agw.Agents.Contracts.Execution;
using Agw.Shared.Exceptions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Agw.A2A;

/// <summary>
/// copy from A2AServer
/// </summary>
public class AgwA2ARequestHandler : IAgwA2ARequestHandler, IAsyncDisposable
{
    private readonly ITaskStore _taskStore;
    private readonly IServiceScopeFactory _serviceScopeFactory;
    private readonly AgwChannelEventNotifier _notifier;
    private readonly ILogger<A2AServer> _logger;
    private readonly A2AServerOptions _options;

    // NOTE: Concurrent SendMessage requests for the same TaskId is not a supported
    // scenario by the A2A protocol or this SDK. The atomic GetOrAdd/AddOrUpdate
    // patterns used below are defense-in-depth to prevent silent resource leaks.
    private readonly ConcurrentDictionary<string, CancellationTokenSource> _backgroundCancellations = new();

    private readonly ConcurrentDictionary<string, Task> _backgroundTasks = new();

    /// <summary>Initializes a new instance of the <see cref="A2AServer"/> class.</summary>
    /// <param name="taskStore">The task store used for persistence.</param>
    /// <param name="notifier">The event notifier for live event streaming and per-task locking.</param>
    /// <param name="logger">The logger instance.</param>
    /// <param name="serviceScopeFactory"></param>
    /// <param name="options">Optional configuration options.</param>
    public AgwA2ARequestHandler(
        ITaskStore taskStore,
        AgwChannelEventNotifier notifier,
        ILogger<A2AServer> logger,
        IServiceScopeFactory serviceScopeFactory,
        A2AServerOptions? options = null
    )
    {
        _taskStore = taskStore;
        _notifier = notifier ?? throw new AgwException(ErrorCodes.InvalidParam, "notifier cannot be null.");
        _logger = logger ?? throw new AgwException(ErrorCodes.InvalidParam, "logger cannot be null.");
        _options = options ?? new A2AServerOptions();
        _serviceScopeFactory = serviceScopeFactory;
    }

    /// <summary>Cancels and awaits all background return-immediately drain tasks.</summary>
    public async ValueTask DisposeAsync()
    {
        // Cancel all background work. Each drain's finally block owns its own
        // dictionary removal and CTS disposal, so no cleanup needed here.
        foreach (var cts in _backgroundCancellations.Values.ToArray())
        {
            try
            {
                await cts.CancelAsync().ConfigureAwait(false);
            }
            catch (ObjectDisposedException) { }
        }

        await Task.WhenAll(_backgroundTasks.Values.ToArray()).ConfigureAwait(false);

        GC.SuppressFinalize(this);
    }

    /// <inheritdoc />
    public virtual async Task<SendMessageResponse> SendMessageAsync(
        string agentName,
        SendMessageRequest request,
        CancellationToken cancellationToken = default
    )
    {
        using var activity = AgwA2ADiagnostics.Source.StartActivity("A2AServer.SendMessage", ActivityKind.Internal);
        var stopwatch = Stopwatch.StartNew();
        RequestContext? context = null;
        CancellationTokenSource? backgroundCts = null;

        try
        {
            AgwA2ADiagnostics.RequestCount.Add(1);

            context = await ResolveContextAsync(request, streamingResponse: false, cancellationToken)
                .ConfigureAwait(false);
            TagActivity(activity, context);
            GuardTerminalState(context);

            if (context.IsContinuation && _options.AutoAppendHistory)
            {
                await ApplyEventAsync(new StreamResponse { Message = request.Message }, context, cancellationToken)
                    .ConfigureAwait(false);
            }

            bool returnImmediately = request.Configuration?.ReturnImmediately == true;

            var eventQueue = new AgentEventQueue();
            // For return-immediately, use a long-lived CTS that outlives the HTTP request
            // but can be cancelled by a subsequent tasks/cancel call.
            // For continuations, reuse the existing CTS so one cancel kills all handlers.
            CancellationToken executionCancellationToken;
            if (returnImmediately)
            {
                // NOTE: Concurrent SendMessage requests for the same TaskId is not a
                // supported scenario by either the A2A protocol or this SDK. The atomic
                // GetOrAdd pattern below is defense-in-depth — it prevents silent CTS
                // orphaning if the unsupported scenario occurs, rather than enabling it.
                var newCts = new CancellationTokenSource();
                var cts = _backgroundCancellations.GetOrAdd(context.TaskId, newCts);

                if (ReferenceEquals(cts, newCts))
                {
                    // We won the race — this drain will own CTS disposal.
                    backgroundCts = newCts;
                }
                else
                {
                    // Another request already registered a CTS — reuse it.
                    newCts.Dispose();
                }

                try
                {
                    executionCancellationToken = cts.Token;
                }
                catch (ObjectDisposedException)
                {
                    // The existing CTS was disposed by a completing drain — replace atomically.
                    backgroundCts = new CancellationTokenSource();
                    if (_backgroundCancellations.TryAdd(context.TaskId, backgroundCts))
                    {
                        executionCancellationToken = backgroundCts.Token;
                    }
                    else if (_backgroundCancellations.TryGetValue(context.TaskId, out var current))
                    {
                        // Another thread inserted a fresh CTS — reuse it.
                        backgroundCts.Dispose();
                        backgroundCts = null;
                        executionCancellationToken = current.Token;
                    }
                    else
                    {
                        // Entry was removed between TryAdd and TryGetValue — use ours.
                        _backgroundCancellations.TryAdd(context.TaskId, backgroundCts);
                        executionCancellationToken = backgroundCts.Token;
                    }
                }
            }
            else
            {
                executionCancellationToken = cancellationToken;
            }

            var agentTask = Task.Run(
                async () =>
                {
                    try
                    {
                        await (await GetAgentHandler(agentName))
                            .ExecuteAsync(context, eventQueue, executionCancellationToken)
                            .ConfigureAwait(false);
                    }
                    finally
                    {
                        eventQueue.Complete();
                    }
                },
                executionCancellationToken
            );

            if (returnImmediately)
            {
                return await MaterializeReturnImmediatelyResponseAsync(
                        eventQueue,
                        agentTask,
                        context,
                        backgroundCts,
                        executionCancellationToken,
                        cancellationToken
                    )
                    .ConfigureAwait(false);
            }

            var result = await MaterializeResponseAsync(eventQueue, context, cancellationToken).ConfigureAwait(false);
            await agentTask.ConfigureAwait(false); // surface handler exceptions

            return result;
        }
        catch (Exception ex)
        {
            // Clean up orphaned background CTS if we registered one but never
            // reached the drain task that owns its removal and disposal.
            if (
                backgroundCts is not null
                && context is not null
                && _backgroundCancellations.TryRemove(context.TaskId, out _)
            )
            {
                backgroundCts.Dispose();
            }

            AgwA2ADiagnostics.ErrorCount.Add(1);
            activity?.SetStatus(ActivityStatusCode.Error, ex.Message);
            RecordException(activity, ex);
            throw;
        }
        finally
        {
            AgwA2ADiagnostics.RequestDuration.Record(stopwatch.Elapsed.TotalMilliseconds);
        }
    }

    /// <inheritdoc />
    public virtual async IAsyncEnumerable<StreamResponse> SendStreamingMessageAsync(
        string agentName,
        SendMessageRequest request,
        [EnumeratorCancellation] CancellationToken cancellationToken = default
    )
    {
        using var activity = AgwA2ADiagnostics.Source.StartActivity(
            "A2AServer.SendStreamingMessage",
            ActivityKind.Internal
        );
        AgwA2ADiagnostics.RequestCount.Add(1);

        RequestContext? context = null;
        AgentEventQueue? eventQueue = null;
        Task? agentTask = null;
        int eventCount = 0;

        try
        {
            context = await ResolveContextAsync(request, streamingResponse: true, cancellationToken)
                .ConfigureAwait(false);
            TagActivity(activity, context);
            GuardTerminalState(context);

            if (context.IsContinuation && _options.AutoAppendHistory)
            {
                await ApplyEventAsync(new StreamResponse { Message = request.Message }, context, cancellationToken)
                    .ConfigureAwait(false);
            }

            eventQueue = new AgentEventQueue();
            agentTask = Task.Run(
                async () =>
                {
                    try
                    {
                        await (await GetAgentHandler(agentName))
                            .ExecuteAsync(context, eventQueue, cancellationToken)
                            .ConfigureAwait(false);
                    }
                    finally
                    {
                        eventQueue.Complete();
                    }
                },
                cancellationToken
            );
        }
        catch (Exception ex)
        {
            AgwA2ADiagnostics.ErrorCount.Add(1);
            activity?.SetStatus(ActivityStatusCode.Error, ex.Message);
            RecordException(activity, ex);
            throw;
        }

        try
        {
            await foreach (var response in eventQueue.WithCancellation(cancellationToken).ConfigureAwait(false))
            {
                try
                {
                    if (!await ApplyEventAsync(response, context!, cancellationToken).ConfigureAwait(false))
                    {
                        continue;
                    }
                }
                catch (Exception ex)
                {
                    AgwA2ADiagnostics.ErrorCount.Add(1);
                    activity?.SetStatus(ActivityStatusCode.Error, ex.Message);
                    RecordException(activity, ex);
                    yield break;
                }

                eventCount++;
                yield return response;
            }

            // Surface any agent exceptions
            await agentTask!.ConfigureAwait(false);
        }
        finally
        {
            AgwA2ADiagnostics.StreamEventCount.Record(eventCount);
        }
    }

    /// <inheritdoc />
    public virtual async Task<AgentTask> GetTaskAsync(
        GetTaskRequest request,
        CancellationToken cancellationToken = default
    )
    {
        if (request.HistoryLength is { } hl && hl < 0)
        {
            throw new AgwException(
                ErrorCodes.InvalidHistoryLength,
                $"Invalid historyLength: {hl}. Must be non-negative."
            );
        }

        var task =
            await _taskStore.GetTaskAsync(request.Id, cancellationToken).ConfigureAwait(false)
            ?? throw new AgwException(ErrorCodes.A2ATaskNotFound, $"Task '{request.Id}' not found.");

        return task.WithHistoryTrimmedTo(request.HistoryLength);
    }

    /// <inheritdoc />
    public virtual async Task<ListTasksResponse> ListTasksAsync(
        string agentName,
        ListTasksRequest request,
        CancellationToken cancellationToken = default
    )
    {
        // Verification is an effective agent
        await GetAgentHandler(agentName).ConfigureAwait(false);
        return await _taskStore.ListTasksAsync(request, cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public virtual async Task<AgentTask> CancelTaskAsync(
        string agentName,
        CancelTaskRequest request,
        CancellationToken cancellationToken = default
    )
    {
        using var activity = AgwA2ADiagnostics.Source.StartActivity("A2AServer.CancelTask", ActivityKind.Internal);
        activity?.SetTag("a2a.task.id", request.Id);

        var task =
            await _taskStore.GetTaskAsync(request.Id, cancellationToken).ConfigureAwait(false)
            ?? throw new AgwException(ErrorCodes.A2ATaskNotFound, $"Task '{request.Id}' not found.");

        if (task.Status.State.IsTerminal())
        {
            throw new AgwException(ErrorCodes.A2ATaskNotCancelable, "Task is already in a terminal state.");
        }

        await using (var scope = _serviceScopeFactory.CreateAsyncScope())
        {
            var durableBridge = scope.ServiceProvider.GetService<IDurableA2AExecutionBridge>();
            if (durableBridge is { SupportsDurableOperations: true })
            {
                var interrupted = await durableBridge.CancelAsync(request.Id, cancellationToken).ConfigureAwait(false);
                if (!interrupted)
                {
                    throw new AgwException(ErrorCodes.A2ATaskNotCancelable, "Task is no longer cancelable.");
                }
            }
        }

        // Signal any background return-immediately work to stop.
        // Don't dispose the CTS here — the drain's finally block owns disposal.
        if (_backgroundCancellations.TryRemove(request.Id, out var backgroundCts))
        {
            await backgroundCts.CancelAsync().ConfigureAwait(false);
        }

        var context = new RequestContext
        {
            Message =
                task.History?.LastOrDefault()
                ?? new Message
                {
                    Role = Role.User,
                    MessageId = string.Empty,
                    Parts = [],
                },
            Task = task,
            TaskId = task.Id,
            ContextId = task.ContextId,
            StreamingResponse = false,
            Metadata = request.Metadata,
        };

        var eventQueue = new AgentEventQueue();
        var agentTask = Task.Run(
            async () =>
            {
                try
                {
                    await (await GetAgentHandler(agentName))
                        .CancelAsync(context, eventQueue, cancellationToken)
                        .ConfigureAwait(false);
                }
                finally
                {
                    eventQueue.Complete();
                }
            },
            cancellationToken
        );

        await foreach (var response in eventQueue.WithCancellation(cancellationToken).ConfigureAwait(false))
        {
            await ApplyEventAsync(response, context, cancellationToken).ConfigureAwait(false);
        }

        await agentTask.ConfigureAwait(false);

        return await _taskStore.GetTaskAsync(request.Id, cancellationToken).ConfigureAwait(false)
            ?? throw new AgwException(ErrorCodes.A2ATaskNotFound, $"Task '{request.Id}' not found.");
    }

    /// <inheritdoc />
    public virtual async IAsyncEnumerable<StreamResponse> SubscribeToTaskAsync(
        SubscribeToTaskRequest request,
        [EnumeratorCancellation] CancellationToken cancellationToken = default
    )
    {
        using var activity = AgwA2ADiagnostics.Source.StartActivity("A2AServer.SubscribeToTask", ActivityKind.Internal);
        activity?.SetTag("a2a.task.id", request.Id);

        await using var durableScope = _serviceScopeFactory.CreateAsyncScope();
        var durableBridge = durableScope.ServiceProvider.GetService<IDurableA2AExecutionBridge>();
        if (durableBridge is { SupportsDurableOperations: true })
        {
            var durableTask =
                await _taskStore.GetTaskAsync(request.Id, cancellationToken).ConfigureAwait(false)
                ?? throw new AgwException(ErrorCodes.A2ATaskNotFound, $"Task '{request.Id}' not found.");
            if (durableTask.Status.State.IsTerminal())
            {
                throw new AgwException(
                    ErrorCodes.A2ATerminalTaskCannotBeSubscribed,
                    "Task is in a terminal state and cannot be subscribed to."
                );
            }

            yield return new StreamResponse { Task = durableTask };
            var durableContext = new RequestContext
            {
                Message =
                    durableTask.History?.LastOrDefault()
                    ?? new Message
                    {
                        Role = Role.User,
                        MessageId = string.Empty,
                        Parts = [],
                    },
                Task = durableTask,
                TaskId = durableTask.Id,
                ContextId = durableTask.ContextId,
                StreamingResponse = true,
            };
            await foreach (
                var message in durableBridge
                    .SubscribeAsync(request.Id, cursor: null, cancellationToken)
                    .ConfigureAwait(false)
            )
            {
                var eventQueue = new AgentEventQueue();
                var updater = new TaskUpdater(eventQueue, request.Id, durableTask.ContextId);
                if (AgentExecutionMessageProtocol.TryGetFinishedStatus(message, out var status))
                {
                    await CommonAgentHandler
                        .ApplyTerminalStatusAsync(updater, durableContext, status, cancellationToken)
                        .ConfigureAwait(false);
                }
                else
                {
                    await CommonAgentHandler
                        .PublishArtifactsAsync(updater, message, cancellationToken)
                        .ConfigureAwait(false);
                }
                eventQueue.Complete();
                await foreach (var response in eventQueue.WithCancellation(cancellationToken).ConfigureAwait(false))
                {
                    if (!await ApplyEventAsync(response, durableContext, cancellationToken).ConfigureAwait(false))
                    {
                        continue;
                    }
                    yield return response;
                }
            }
            yield break;
        }

        AgentTask currentTask;
        Channel<StreamResponse> channel;

        // Atomic: read task state + register subscriber channel under per-task lock.
        // Concurrent ApplyEventAsync calls block until the channel is registered,
        // guaranteeing no events are lost between snapshot and live stream.
        using (await _notifier.AcquireTaskLockAsync(request.Id, cancellationToken).ConfigureAwait(false))
        {
            currentTask =
                await _taskStore.GetTaskAsync(request.Id, cancellationToken).ConfigureAwait(false)
                ?? throw new AgwException(ErrorCodes.A2ATaskNotFound, $"Task '{request.Id}' not found.");

            if (currentTask.Status.State.IsTerminal())
            {
                throw new AgwException(
                    ErrorCodes.A2ATerminalTaskCannotBeSubscribed,
                    "Task is in a terminal state and cannot be subscribed to."
                );
            }

            channel = _notifier.CreateChannel(request.Id);
        }

        // First event MUST be current Task object (spec §3.1.6)
        yield return new StreamResponse { Task = currentTask };

        // Live events via channel (no catch-up needed — lock guarantees no gap)
        try
        {
            await foreach (var streamEvent in channel.Reader.ReadAllAsync(cancellationToken).ConfigureAwait(false))
            {
                yield return streamEvent;
            }
        }
        finally
        {
            _notifier.RemoveChannel(request.Id, channel);
        }
    }

    /// <inheritdoc />
    public virtual Task<TaskPushNotificationConfig> CreateTaskPushNotificationConfigAsync(
        CreateTaskPushNotificationConfigRequest request,
        CancellationToken cancellationToken = default
    )
    {
        throw new AgwException(ErrorCodes.A2APushNotificationNotSupported, "Push notifications not supported.");
    }

    /// <inheritdoc />
    public virtual Task<TaskPushNotificationConfig> GetTaskPushNotificationConfigAsync(
        GetTaskPushNotificationConfigRequest request,
        CancellationToken cancellationToken = default
    )
    {
        throw new AgwException(ErrorCodes.A2APushNotificationNotSupported, "Push notifications not supported.");
    }

    /// <inheritdoc />
    public virtual Task<ListTaskPushNotificationConfigResponse> ListTaskPushNotificationConfigAsync(
        ListTaskPushNotificationConfigRequest request,
        CancellationToken cancellationToken = default
    )
    {
        throw new AgwException(ErrorCodes.A2APushNotificationNotSupported, "Push notifications not supported.");
    }

    /// <inheritdoc />
    public virtual Task DeleteTaskPushNotificationConfigAsync(
        DeleteTaskPushNotificationConfigRequest request,
        CancellationToken cancellationToken = default
    )
    {
        throw new AgwException(ErrorCodes.A2APushNotificationNotSupported, "Push notifications not supported.");
    }

    /// <inheritdoc />
    public virtual Task<AgentCard> GetExtendedAgentCardAsync(
        GetExtendedAgentCardRequest request,
        CancellationToken cancellationToken = default
    )
    {
        throw new AgwException(ErrorCodes.A2AExtendedAgentCardNotConfigured, "Extended agent card not configured.");
    }

    // ─── Private Helpers ───

    private async Task<RequestContext> ResolveContextAsync(
        SendMessageRequest request,
        bool streamingResponse,
        CancellationToken cancellationToken
    )
    {
        AgentTask? existingTask = null;
        var taskId = request.Message.TaskId;
        var contextId = request.Message.ContextId;

        if (!string.IsNullOrEmpty(taskId))
        {
            existingTask =
                await _taskStore.GetTaskAsync(taskId, cancellationToken).ConfigureAwait(false)
                ?? throw new AgwException(ErrorCodes.A2ATaskNotFound, $"Task '{taskId}' not found.");
            contextId ??= existingTask.ContextId;
        }

        return new RequestContext
        {
            Message = request.Message,
            Task = existingTask,
            TaskId = taskId ?? Guid.CreateVersion7().ToString("N"),
            ContextId = contextId ?? Guid.CreateVersion7().ToString("N"),
            ClientProvidedContextId = contextId is not null,
            StreamingResponse = streamingResponse,
            Configuration = request.Configuration,
            Metadata = request.Metadata,
        };
    }

    private static void GuardTerminalState(RequestContext context)
    {
        if (context.Task is not null && context.Task.Status.State.IsTerminal())
        {
            throw new AgwException(
                ErrorCodes.A2ATerminalTaskCannotAcceptMessages,
                "Task is in a terminal state and cannot accept messages."
            );
        }
    }

    private async Task<bool> ApplyEventAsync(
        StreamResponse response,
        RequestContext context,
        CancellationToken cancellationToken
    )
    {
        using (await _notifier.AcquireTaskLockAsync(context.TaskId, cancellationToken).ConfigureAwait(false))
        {
            var currentTask = await _taskStore.GetTaskAsync(context.TaskId, cancellationToken).ConfigureAwait(false);

            var incomingState = response.StatusUpdate?.Status.State ?? response.Task?.Status.State;
            if (currentTask?.Status.State.IsTerminal() == true && incomingState?.IsTerminal() == true)
            {
                if (currentTask.Status.State != incomingState.Value)
                {
                    _logger.LogWarning(
                        "Ignoring conflicting terminal transition for A2A task {TaskId}: {CurrentState} -> {IncomingState}.",
                        context.TaskId,
                        currentTask.Status.State,
                        incomingState.Value
                    );
                    return false;
                }

                return true;
            }

            var updatedTask = global::A2A.TaskProjection.Apply(currentTask, response);

            // Message-only responses with no existing task have nothing to persist.
            if (updatedTask is null)
            {
                _notifier.Notify(context.TaskId, response);
                return true;
            }

            if (currentTask is null)
            {
                AgwA2ADiagnostics.TaskCreatedCount.Add(1);
            }

            await _taskStore.SaveTaskAsync(context.TaskId, updatedTask, cancellationToken).ConfigureAwait(false);

            _notifier.Notify(context.TaskId, response);
            return true;
        }
    }

    /// <summary>
    /// Reads events until the first Task event, returns it immediately, and drains
    /// remaining events in the background. Message responses are returned normally
    /// (return-immediately has no effect on Message responses per spec).
    /// </summary>
    /// <param name="eventQueue">The event queue produced by the agent handler.</param>
    /// <param name="agentTask">The background task running the agent handler.</param>
    /// <param name="context">The request context for the current operation.</param>
    /// <param name="ownedBackgroundCts">The CTS to dispose on completion, or null if reusing an existing CTS owned by a prior drain.</param>
    /// <param name="backgroundCancellationToken">Token for cancelling the background drain (from shared or new CTS).</param>
    /// <param name="cancellationToken">Token for cancelling the initial read.</param>
    private async Task<SendMessageResponse> MaterializeReturnImmediatelyResponseAsync(
        AgentEventQueue eventQueue,
        Task agentTask,
        RequestContext context,
        CancellationTokenSource? ownedBackgroundCts,
        CancellationToken backgroundCancellationToken,
        CancellationToken cancellationToken
    )
    {
        SendMessageResponse? result = null;

        await foreach (var response in eventQueue.WithCancellation(cancellationToken).ConfigureAwait(false))
        {
            if (!await ApplyEventAsync(response, context, CancellationToken.None).ConfigureAwait(false))
            {
                continue;
            }

            if (result is null)
            {
                if (response.Task is not null)
                {
                    result = new SendMessageResponse { Task = response.Task };
                    break; // Return immediately with first Task event
                }

                if (response.Message is not null)
                {
                    // Message response — returnImmediately has no effect, drain normally
                    result = new SendMessageResponse { Message = response.Message };
                }
            }
        }

        if (result?.Task is not null)
        {
            // Drain remaining events in the background so they are applied to the task store.
            var drainTask = Task.Run(
                async () =>
                {
                    try
                    {
                        await foreach (
                            var response in eventQueue
                                .WithCancellation(backgroundCancellationToken)
                                .ConfigureAwait(false)
                        )
                        {
                            await ApplyEventAsync(response, context, CancellationToken.None).ConfigureAwait(false);
                        }

#pragma warning disable VSTHRD003 // Intentional: agentTask runs the agent handler in the background
                        await agentTask.ConfigureAwait(false);
#pragma warning restore VSTHRD003
                    }
                    catch (OperationCanceledException)
                    {
                        // Expected when tasks/cancel triggers the backgroundCts
                    }
                    catch (Exception ex)
                    {
                        _logger.BackgroundEventProcessingFailed(ex, context.TaskId);
                    }
                    finally
                    {
                        _backgroundTasks.TryRemove(context.TaskId, out _);

                        // Only clean up the CTS if this drain owns it (i.e., it was created
                        // for this request, not reused from an earlier return-immediately call).
                        if (ownedBackgroundCts is not null)
                        {
                            _backgroundCancellations.TryRemove(context.TaskId, out _);
                            ownedBackgroundCts.Dispose();
                        }
                    }
                },
                CancellationToken.None
            );

#pragma warning disable CS4014, VSTHRD003 // Fire-and-forget by design; combined task is tracked for DisposeAsync
            _backgroundTasks.AddOrUpdate(
                context.TaskId,
                drainTask,
                (_, existingDrain) => Task.WhenAll(existingDrain, drainTask)
            );
#pragma warning restore CS4014, VSTHRD003

            // Re-fetch from store to return the current persisted state
            result.Task =
                await _taskStore.GetTaskAsync(context.TaskId, CancellationToken.None).ConfigureAwait(false)
                ?? throw new AgwException(
                    ErrorCodes.A2ATaskNotFound,
                    $"Task '{context.TaskId}' not found after processing."
                );

            return result;
        }

        // Message response or queue fully drained — wait for handler to surface exceptions
#pragma warning disable VSTHRD003 // Intentional: agentTask was started within SendMessageAsync
        await agentTask.ConfigureAwait(false);
#pragma warning restore VSTHRD003

        return result
            ?? throw new AgwException(
                ErrorCodes.A2AInvalidAgentResponse,
                "Agent handler did not produce any response events."
            );
    }

    private async Task<SendMessageResponse> MaterializeResponseAsync(
        AgentEventQueue eventQueue,
        RequestContext context,
        CancellationToken cancellationToken
    )
    {
        SendMessageResponse? result = null;

        await foreach (var response in eventQueue.WithCancellation(cancellationToken).ConfigureAwait(false))
        {
            if (!await ApplyEventAsync(response, context, cancellationToken).ConfigureAwait(false))
            {
                continue;
            }

            // Capture the first Task or Message as the synchronous response
            if (result is null)
            {
                if (response.Task is not null)
                {
                    result = new SendMessageResponse { Task = response.Task };
                }
                else if (response.Message is not null)
                {
                    result = new SendMessageResponse { Message = response.Message };
                }
            }
        }

        // Re-fetch the projected task to ensure the response reflects
        // all persisted events, not a stale snapshot.
        if (result?.Task is not null)
        {
            result.Task =
                await _taskStore.GetTaskAsync(context.TaskId, cancellationToken).ConfigureAwait(false)
                ?? throw new AgwException(
                    ErrorCodes.A2ATaskNotFound,
                    $"Task '{context.TaskId}' not found after processing."
                );
        }

        return result
            ?? throw new AgwException(
                ErrorCodes.A2AInvalidAgentResponse,
                "Agent handler did not produce any response events."
            );
    }

    private async Task<IAgentHandler> GetAgentHandler(string agentName)
    {
        var sp = _serviceScopeFactory.CreateScope().ServiceProvider;
        var ahf = sp.GetRequiredService<AgentHandlerFactory>();
        var ah =
            (await ahf.CreateAsync(agentName))
            ?? throw new AgwException(
                ErrorCodes.A2AInvalidAgentResponse,
                $"No agent handler configured for agent '{agentName}'."
            );
        return ah;
    }

    private static void TagActivity(Activity? activity, RequestContext context)
    {
        activity?.SetTag("a2a.task.id", context.TaskId);
        activity?.SetTag("a2a.context.id", context.ContextId);
        activity?.SetTag("a2a.is_continuation", context.IsContinuation);
        activity?.SetTag("a2a.streaming_response", context.StreamingResponse);
    }

    private static void RecordException(Activity? activity, Exception ex)
    {
        if (activity is null)
        {
            return;
        }

        var tags = new ActivityTagsCollection
        {
            { "exception.type", ex.GetType().FullName },
            { "exception.message", ex.Message },
        };

        activity.AddEvent(new ActivityEvent("exception", tags: tags));
    }
}
