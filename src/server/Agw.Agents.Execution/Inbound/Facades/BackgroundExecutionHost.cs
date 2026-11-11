using System.Collections.Concurrent;
using System.Security.Claims;
using System.Threading.Channels;
using Agw.Agents.Application.Persistence;
using Agw.Agents.Execution.Configuration;
using Agw.Agents.Execution.Turns;
using Agw.Auth.Contracts;
using Agw.Shared.Data.Entities.Agents;
using Agw.Shared.Exceptions;
using Agw.Shared.Runtime;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Agw.Agents.Execution.Inbound.Facades;

internal sealed class BackgroundExecutionHost : BackgroundService
{
    private static readonly TimeSpan RecoveryInterval = TimeSpan.FromSeconds(30);

    private readonly IServiceScopeFactory _scopeFactory;
    private readonly IServerInitializationState _initializationState;
    private readonly TimeProvider _timeProvider;
    private readonly ExecutionProvider _provider;
    private readonly ILogger<BackgroundExecutionHost> _logger;
    private readonly Channel<BackgroundExecutionWork> _work = Channel.CreateUnbounded<BackgroundExecutionWork>(
        new UnboundedChannelOptions { SingleReader = true, SingleWriter = false }
    );
    private readonly ConcurrentDictionary<Guid, CancellationTokenSource> _running = new();
    private readonly ConcurrentDictionary<Guid, byte> _monitoring = new();

    public BackgroundExecutionHost(
        IServiceScopeFactory scopeFactory,
        IServerInitializationState initializationState,
        TimeProvider timeProvider,
        IOptions<ExecutionRuntimeOptions> options,
        ILogger<BackgroundExecutionHost> logger
    )
    {
        _scopeFactory = scopeFactory;
        _initializationState = initializationState;
        _timeProvider = timeProvider;
        _provider = options.Value.Provider;
        _logger = logger;
    }

    public void Enqueue(AcceptedTurn accepted, string ownerUserId)
    {
        var cancellation = new CancellationTokenSource();
        if (!_running.TryAdd(accepted.Request.TurnId, cancellation))
        {
            cancellation.Dispose();
            throw new AgwException(ErrorCodes.DurableExecutionConflict, "The background execution is already queued.");
        }

        if (!_work.Writer.TryWrite(new BackgroundExecutionWork(accepted, ownerUserId, cancellation)))
        {
            _running.TryRemove(accepted.Request.TurnId, out _);
            cancellation.Dispose();
            throw new AgwException(ErrorCodes.AgentExecutionFailed, "The background execution host is stopping.");
        }
    }

    public bool CancelInProcess(Guid turnId)
    {
        if (!_running.TryGetValue(turnId, out var cancellation))
        {
            return false;
        }

        cancellation.Cancel();
        return true;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!_initializationState.IsInitialized)
        {
            await Task.Delay(TimeSpan.FromMilliseconds(250), _timeProvider, stoppingToken).ConfigureAwait(false);
        }

        if (_provider == ExecutionProvider.InProcess)
        {
            await InterruptPreviousInProcessExecutionsAsync(stoppingToken).ConfigureAwait(false);
        }
        else
        {
            _ = RecoverDistributedExecutionsAsync(stoppingToken);
        }

        await foreach (var work in _work.Reader.ReadAllAsync(stoppingToken).ConfigureAwait(false))
        {
            _ = ExecuteAsync(work, stoppingToken);
        }
    }

    private async Task ExecuteAsync(BackgroundExecutionWork work, CancellationToken stoppingToken)
    {
        using var executionCancellation = CancellationTokenSource.CreateLinkedTokenSource(
            stoppingToken,
            work.Cancellation.Token
        );
        try
        {
            await using var scope = _scopeFactory.CreateAsyncScope();
            var persistence = scope.ServiceProvider.GetRequiredService<IBackgroundExecutionPersistence>();
            var executionFacade = scope.ServiceProvider.GetRequiredService<AgentExecutionFacade>();
            var completion = scope.ServiceProvider.GetRequiredService<IBackgroundExecutionCompletionTransaction>();
            using var userScope = PushUser(work.OwnerUserId);
            if (
                !await persistence
                    .MarkRunningAsync(
                        work.Accepted.Request.TurnId,
                        work.OwnerUserId,
                        _timeProvider.GetUtcNow(),
                        executionCancellation.Token
                    )
                    .ConfigureAwait(false)
            )
            {
                return;
            }

            var result = await executionFacade
                .ExecuteAcceptedAsync(work.Accepted, executionCancellation.Token)
                .ConfigureAwait(false);
            await CompleteAsync(completion, ToCompletion(result, work.OwnerUserId), stoppingToken)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (work.Cancellation.IsCancellationRequested)
        {
            await CompleteInterruptedAsync(work, "The background execution was interrupted.").ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            if (_provider == ExecutionProvider.InProcess)
            {
                await CompleteInterruptedAsync(work, "The server stopped during in-process execution.")
                    .ConfigureAwait(false);
            }
        }
        catch (Exception exception)
        {
            _logger.LogError(exception, "Background execution {TurnId} failed.", work.Accepted.Request.TurnId);
            await CompleteFailedAsync(work, exception).ConfigureAwait(false);
        }
        finally
        {
            _running.TryRemove(work.Accepted.Request.TurnId, out _);
            work.Cancellation.Dispose();
        }
    }

    private async Task InterruptPreviousInProcessExecutionsAsync(CancellationToken cancellationToken)
    {
        await using var scope = _scopeFactory.CreateAsyncScope();
        var persistence = scope.ServiceProvider.GetRequiredService<IBackgroundExecutionPersistence>();
        var completion = scope.ServiceProvider.GetRequiredService<IBackgroundExecutionCompletionTransaction>();
        var executions = await persistence.ListNonTerminalAsync(cancellationToken).ConfigureAwait(false);
        foreach (var execution in executions)
        {
            var ownerUserId = ResolveOwnerUserId(execution.CreateBy);
            await completion
                .CompleteAsync(
                    new BackgroundExecutionCompletion
                    {
                        TurnId = execution.Id,
                        OwnerUserId = ownerUserId,
                        State = AgentExecutionState.Interrupted,
                        ResultText = execution.ResultText,
                        Error = "The server restarted during in-process execution.",
                        InputTokens = execution.InputTokens,
                        OutputTokens = execution.OutputTokens,
                        CompletedAt = _timeProvider.GetUtcNow(),
                    },
                    cancellationToken
                )
                .ConfigureAwait(false);
        }
    }

    private async Task RecoverDistributedExecutionsAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                await using var scope = _scopeFactory.CreateAsyncScope();
                var persistence = scope.ServiceProvider.GetRequiredService<IBackgroundExecutionPersistence>();
                var executions = await persistence.ListNonTerminalAsync(cancellationToken).ConfigureAwait(false);
                foreach (var execution in executions)
                {
                    if (_monitoring.TryAdd(execution.Id, 0))
                    {
                        _ = MonitorDistributedAsync(execution, cancellationToken);
                    }
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception exception)
            {
                _logger.LogError(exception, "Failed to recover distributed background executions.");
            }

            await Task.Delay(RecoveryInterval, _timeProvider, cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task MonitorDistributedAsync(BackgroundExecution execution, CancellationToken cancellationToken)
    {
        try
        {
            await using var scope = _scopeFactory.CreateAsyncScope();
            var durable = scope.ServiceProvider.GetRequiredService<IDurableAgentExecutionFacade>();
            var completion = scope.ServiceProvider.GetRequiredService<IBackgroundExecutionCompletionTransaction>();
            var messages = new List<AgwMessage>();
            AgentExecutionResult outcome;
            var ownerUserId = ResolveOwnerUserId(execution.CreateBy);
            try
            {
                await foreach (
                    var item in durable
                        .SubscribeAsync(execution.Id, ownerUserId, afterCursor: null, cancellationToken)
                        .ConfigureAwait(false)
                )
                {
                    messages.Add(item.Message);
                }

                outcome = await durable
                    .GetOutcomeAsync(execution.Id, ownerUserId, cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (AgwException exception) when (exception.Code == ErrorCodes.DurableExecutionNotFound.Code)
            {
                await CompleteRecordAsync(
                        completion,
                        execution,
                        AgentExecutionState.Interrupted,
                        "The durable execution was not registered before recovery.",
                        messages,
                        cancellationToken
                    )
                    .ConfigureAwait(false);
                return;
            }

            if (outcome.State == AgentExecutionState.WaitingForHuman)
            {
                await durable
                    .InterruptAsync(
                        execution.Id,
                        ownerUserId,
                        "Background executions do not support human interaction.",
                        cancellationToken
                    )
                    .ConfigureAwait(false);
                await CompleteRecordAsync(
                        completion,
                        execution,
                        AgentExecutionState.Interrupted,
                        "Background executions do not support human interaction.",
                        messages,
                        cancellationToken
                    )
                    .ConfigureAwait(false);
                return;
            }

            if (outcome.State is AgentExecutionState.Queued or AgentExecutionState.Running)
            {
                return;
            }

            await CompleteRecordAsync(
                    completion,
                    execution,
                    outcome.State,
                    outcome.ErrorMessage,
                    messages,
                    cancellationToken
                )
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { }
        catch (Exception exception)
        {
            _logger.LogError(exception, "Failed to monitor distributed background execution {TurnId}.", execution.Id);
        }
        finally
        {
            _monitoring.TryRemove(execution.Id, out _);
        }
    }

    private async Task CompleteInterruptedAsync(BackgroundExecutionWork work, string error)
    {
        await using var scope = _scopeFactory.CreateAsyncScope();
        var completion = scope.ServiceProvider.GetRequiredService<IBackgroundExecutionCompletionTransaction>();
        await CompleteAsync(
                completion,
                new BackgroundExecutionCompletion
                {
                    TurnId = work.Accepted.Request.TurnId,
                    OwnerUserId = work.OwnerUserId,
                    State = AgentExecutionState.Interrupted,
                    Error = error,
                    InputTokens = 0,
                    OutputTokens = 0,
                    CompletedAt = _timeProvider.GetUtcNow(),
                },
                CancellationToken.None
            )
            .ConfigureAwait(false);
    }

    private async Task CompleteFailedAsync(BackgroundExecutionWork work, Exception exception)
    {
        await using var scope = _scopeFactory.CreateAsyncScope();
        var completion = scope.ServiceProvider.GetRequiredService<IBackgroundExecutionCompletionTransaction>();
        await CompleteAsync(
                completion,
                new BackgroundExecutionCompletion
                {
                    TurnId = work.Accepted.Request.TurnId,
                    OwnerUserId = work.OwnerUserId,
                    State = AgentExecutionState.Failed,
                    Error = exception.Message,
                    InputTokens = 0,
                    OutputTokens = 0,
                    CompletedAt = _timeProvider.GetUtcNow(),
                },
                CancellationToken.None
            )
            .ConfigureAwait(false);
    }

    private async Task CompleteRecordAsync(
        IBackgroundExecutionCompletionTransaction completion,
        BackgroundExecution execution,
        AgentExecutionState state,
        string? error,
        IReadOnlyList<AgwMessage> messages,
        CancellationToken cancellationToken
    ) =>
        await completion
            .CompleteAsync(
                ToCompletion(execution.Id, ResolveOwnerUserId(execution.CreateBy), state, error, messages),
                cancellationToken
            )
            .ConfigureAwait(false);

    private async Task CompleteAsync(
        IBackgroundExecutionCompletionTransaction completion,
        BackgroundExecutionCompletion result,
        CancellationToken cancellationToken
    ) => await completion.CompleteAsync(result, cancellationToken).ConfigureAwait(false);

    private BackgroundExecutionCompletion ToCompletion(AgentExecutionResult result, string ownerUserId) =>
        ToCompletion(result.TurnId, ownerUserId, result.State, result.ErrorMessage, result.Messages);

    private BackgroundExecutionCompletion ToCompletion(
        Guid turnId,
        string ownerUserId,
        AgentExecutionState state,
        string? error,
        IReadOnlyList<AgwMessage> messages
    )
    {
        var usage = messages.SelectMany(message => message.Contents).OfType<AgwUsageContent>().ToArray();
        var text = messages
            .Where(message => message.Role == AiRole.Assistant)
            .SelectMany(message => message.Contents.OfType<AgwTextContent>())
            .Select(content => content.Content)
            .LastOrDefault(content => !string.IsNullOrWhiteSpace(content));
        var lastError = messages
            .SelectMany(message => message.Contents)
            .OfType<AgwErrorContent>()
            .Select(content => content.Content)
            .LastOrDefault(content => !string.IsNullOrWhiteSpace(content));
        return new BackgroundExecutionCompletion
        {
            TurnId = turnId,
            OwnerUserId = ownerUserId,
            State = state,
            ResultText = text,
            Error = error ?? lastError,
            InputTokens = usage.Sum(content => content.Content.InputTokenCount ?? 0),
            OutputTokens = usage.Sum(content => content.Content.OutputTokenCount ?? 0),
            CompletedAt = _timeProvider.GetUtcNow(),
        };
    }

    private static IDisposable PushUser(string userId) =>
        UserInfoUtil.Push(
            new ClaimsPrincipal(
                new ClaimsIdentity([new Claim(ClaimTypes.NameIdentifier, userId)], "BackgroundExecution")
            )
        );

    private static string ResolveOwnerUserId(string? userId) =>
        string.IsNullOrWhiteSpace(userId)
            ? throw new AgwException(ErrorCodes.AuthenticationRequired, "The background execution owner is missing.")
            : userId.Trim();

    private sealed record BackgroundExecutionWork(
        AcceptedTurn Accepted,
        string OwnerUserId,
        CancellationTokenSource Cancellation
    );
}
