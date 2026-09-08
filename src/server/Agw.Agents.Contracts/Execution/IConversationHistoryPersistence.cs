using System.Runtime.CompilerServices;
using Agw.Projects.Contracts;

namespace Agw.Agents.Contracts.Execution;

/// <summary>Owns the pending history of one execution, independently of its transport.</summary>
public interface IConversationHistoryPersistence
{
    IConversationHistoryPersistenceScope BeginScope(
        Guid projectId,
        string contextId,
        int generation,
        CancellationToken ownershipLost = default,
        bool allowCreateConversation = false
    );
}

public interface IConversationHistoryPersistenceScope
{
    Task FlushAsync(CancellationToken cancellationToken = default);
    Task<IAsyncDisposable> EnterBarrierAsync(CancellationToken cancellationToken = default);
    ValueTask CompleteAsync(Exception? executionFailure);
}

/// <summary>Flows history through nested Agents without serializing buffers into SDK state.</summary>
public static class ConversationHistoryPersistenceContext
{
    private static readonly AsyncLocal<ScopeFrame?> Ambient = new();

    public static IConversationHistoryPersistenceScope? Current => Ambient.Value?.Scope;
    public static bool HasExecutionFailure => Ambient.Value?.Failure != null;

    /// <summary>Starts a scope for a Task-based execution. Async iterators must use RunStreaming.</summary>
    public static IAsyncDisposable BeginScope(
        IConversationHistoryPersistence? persistence,
        Guid projectId,
        string contextId,
        int generation,
        CancellationToken ownershipLost = default,
        bool allowCreateConversation = false
    )
    {
        contextId = ContextIdUtil.NormalizeContextId(contextId);
        var scope = persistence?.BeginScope(projectId, contextId, generation, ownershipLost, allowCreateConversation);
        if (scope == null || ReferenceEquals(Current, scope))
            return new EmptyLease();
        var frame = new ScopeFrame(scope, projectId, contextId, generation);
        return new ScopeLease(frame, Activate(frame));
    }

    /// <summary>
    /// Re-establishes the ambient scope on every MoveNext and Dispose. AsyncLocal changes inside an iterator
    /// do not survive returning a yielded item to a caller with a different ExecutionContext.
    /// </summary>
    public static IAsyncEnumerable<T> RunStreaming<T>(
        IAsyncEnumerable<T> execution,
        IConversationHistoryPersistence? persistence,
        Guid projectId,
        string contextId,
        int generation,
        bool allowCreateConversation = false
    ) =>
        persistence == null
            ? execution
            : new ScopedEnumerable<T>(
                execution,
                persistence,
                projectId,
                ContextIdUtil.NormalizeContextId(contextId),
                generation,
                allowCreateConversation
            );

    public static Task FlushAsync(CancellationToken cancellationToken = default) =>
        Current?.FlushAsync(cancellationToken) ?? Task.CompletedTask;

    public static Task<IAsyncDisposable> EnterBarrierAsync(CancellationToken cancellationToken = default) =>
        Current?.EnterBarrierAsync(cancellationToken) ?? Task.FromResult<IAsyncDisposable>(new EmptyLease());

    public static void RecordFailure(Exception exception)
    {
        if (Ambient.Value is { } frame && !ReferenceEquals(frame.Interruption, exception))
            frame.Failure ??= exception;
    }

    /// <summary>Marks an intentional runtime interruption, such as a durable human wait, rather than a failed execution.</summary>
    public static void IgnoreInterruption(Exception interruption)
    {
        if (Ambient.Value is { } frame)
            frame.Interruption = interruption;
    }

    public static async Task<T> ObserveAsync<T>(Task<T> execution)
    {
        try
        {
            return await execution.ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            RecordFailure(exception);
            throw;
        }
    }

    public static async IAsyncEnumerable<T> ObserveAsync<T>(
        IAsyncEnumerable<T> execution,
        [EnumeratorCancellation] CancellationToken cancellationToken = default
    )
    {
        IAsyncEnumerator<T> enumerator;
        try
        {
            enumerator = execution.GetAsyncEnumerator(cancellationToken);
        }
        catch (Exception exception)
        {
            RecordFailure(exception);
            throw;
        }
        Exception? failure = null;
        try
        {
            while (true)
            {
                bool hasNext;
                try
                {
                    hasNext = await enumerator.MoveNextAsync().ConfigureAwait(false);
                }
                catch (Exception exception)
                {
                    failure = exception;
                    RecordFailure(exception);
                    throw;
                }
                if (!hasNext)
                    break;
                yield return enumerator.Current;
            }
        }
        finally
        {
            try
            {
                await enumerator.DisposeAsync().ConfigureAwait(false);
            }
            catch (Exception exception)
            {
                RecordFailure(exception);
                if (failure == null)
                    throw;
            }
        }
    }

    private static IDisposable Activate(ScopeFrame frame)
    {
        var previous = Ambient.Value;
        Ambient.Value = frame;
        return new Activation(
            previous,
            ConversationSessionContext.Push(frame.ProjectId, frame.ContextId, frame.Generation)
        );
    }

    private sealed class ScopeFrame
    {
        public IConversationHistoryPersistenceScope Scope { get; }
        public Guid ProjectId { get; }
        public string ContextId { get; }
        public int Generation { get; }
        public Exception? Failure { get; set; }
        public Exception? Interruption { get; set; }

        public ScopeFrame(IConversationHistoryPersistenceScope scope, Guid projectId, string contextId, int generation)
        {
            Scope = scope;
            ProjectId = projectId;
            ContextId = contextId;
            Generation = generation;
        }
    }

    private sealed class Activation : IDisposable
    {
        private readonly ScopeFrame? _previous;
        private readonly IDisposable _conversationContext;

        public Activation(ScopeFrame? previous, IDisposable conversationContext)
        {
            _previous = previous;
            _conversationContext = conversationContext;
        }

        public void Dispose()
        {
            Ambient.Value = _previous;
            _conversationContext.Dispose();
        }
    }

    private sealed class ScopeLease : IAsyncDisposable
    {
        private readonly ScopeFrame _frame;
        private readonly IDisposable _activation;

        public ScopeLease(ScopeFrame frame, IDisposable activation)
        {
            _frame = frame;
            _activation = activation;
        }

        public ValueTask DisposeAsync()
        {
            // Restore synchronously so the caller, rather than an async continuation, sees its previous scope.
            _activation.Dispose();
            return _frame.Scope.CompleteAsync(_frame.Failure);
        }
    }

    private sealed class ScopedEnumerable<T> : IAsyncEnumerable<T>
    {
        private readonly IAsyncEnumerable<T> _execution;
        private readonly IConversationHistoryPersistence _persistence;
        private readonly Guid _projectId;
        private readonly string _contextId;
        private readonly int _generation;
        private readonly bool _allowCreate;

        public ScopedEnumerable(
            IAsyncEnumerable<T> execution,
            IConversationHistoryPersistence persistence,
            Guid projectId,
            string contextId,
            int generation,
            bool allowCreate
        )
        {
            _execution = execution;
            _persistence = persistence;
            _projectId = projectId;
            _contextId = contextId;
            _generation = generation;
            _allowCreate = allowCreate;
        }

        public IAsyncEnumerator<T> GetAsyncEnumerator(CancellationToken cancellationToken = default)
        {
            var parent = Ambient.Value;
            var scope = _persistence.BeginScope(
                _projectId,
                _contextId,
                _generation,
                allowCreateConversation: _allowCreate
            );
            var ownsScope = !ReferenceEquals(parent?.Scope, scope);
            var frame = ownsScope ? new ScopeFrame(scope, _projectId, _contextId, _generation) : parent!;
            return new ScopedEnumerator<T>(_execution, frame, ownsScope, cancellationToken);
        }
    }

    private sealed class ScopedEnumerator<T> : IAsyncEnumerator<T>
    {
        private readonly IAsyncEnumerable<T> _execution;
        private readonly ScopeFrame _frame;
        private readonly bool _ownsScope;
        private readonly CancellationToken _token;
        private IAsyncEnumerator<T>? _inner;
        private int _disposed;
        public T Current => _inner!.Current;

        public ScopedEnumerator(
            IAsyncEnumerable<T> execution,
            ScopeFrame frame,
            bool ownsScope,
            CancellationToken token
        )
        {
            _execution = execution;
            _frame = frame;
            _ownsScope = ownsScope;
            _token = token;
        }

        public ValueTask<bool> MoveNextAsync()
        {
            using var activation = Activate(_frame);
            return MoveNextCoreAsync();
        }

        private async ValueTask<bool> MoveNextCoreAsync()
        {
            try
            {
                _inner ??= _execution.GetAsyncEnumerator(_token);
                return await _inner.MoveNextAsync().ConfigureAwait(false);
            }
            catch (Exception exception)
            {
                RecordFailure(exception);
                throw;
            }
        }

        public ValueTask DisposeAsync()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0)
                return ValueTask.CompletedTask;
            using var activation = Activate(_frame);
            return DisposeCoreAsync();
        }

        private async ValueTask DisposeCoreAsync()
        {
            try
            {
                if (_inner != null)
                    await _inner.DisposeAsync().ConfigureAwait(false);
            }
            catch (Exception exception)
            {
                RecordFailure(exception);
                throw;
            }
            finally
            {
                if (_ownsScope)
                    await _frame.Scope.CompleteAsync(_frame.Failure).ConfigureAwait(false);
            }
        }
    }

    private sealed class EmptyLease : IAsyncDisposable
    {
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
