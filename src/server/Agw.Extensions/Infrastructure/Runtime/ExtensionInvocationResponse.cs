using System.Security.Claims;
using System.Text.Json;
using Agw.Auth.Contracts;
using Agw.Extensions.Contracts;
using Agw.Extensions.Sdk;
using Agw.Shared.Exceptions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Agw.Extensions.Infrastructure.Runtime;

internal sealed class ExtensionInvocationResponse : IExtensionInvocationResponse
{
    private readonly Guid _extensionId;
    private readonly string _releaseHash;
    private readonly Guid _generationId;
    private readonly object _gate = new();
    private CancellationTokenSource? _streamCancellation = new();
    private BodyStreamAdapter? _bodyStream;
    private ExtensionInvocationResources? _resources;
    private StreamEnumerator? _enumerator;
    private ILogger? _logger;
    private bool _disposed;

    public ExtensionInvocationResponse(
        ExtensionEndpointResponse response,
        ExtensionInvocationResources resources,
        ExtensionGenerationId generationId,
        string releaseHash,
        ILogger logger
    )
    {
        StatusCode = response.StatusCode;
        ContentType = response.ContentType;
        JsonBody = response.JsonBody?.Clone();
        _extensionId = generationId.ExtensionId;
        _generationId = generationId.GenerationId;
        _releaseHash = releaseHash;
        _resources = resources;
        _logger = logger;
        _bodyStream = response.BodyStream is null ? null : new BodyStreamAdapter(this, response.BodyStream);
    }

    public int StatusCode { get; }

    public string ContentType { get; }

    public JsonElement? JsonBody { get; }

    public IAsyncEnumerable<ReadOnlyMemory<byte>>? BodyStream
    {
        get
        {
            lock (_gate)
            {
                return _bodyStream;
            }
        }
    }

    public ValueTask CompleteAsync(CancellationToken cancellationToken = default) => DisposeAsync();

    public async ValueTask DisposeAsync()
    {
        StreamEnumerator? enumerator;
        BodyStreamAdapter? bodyStream;
        CancellationTokenSource? streamCancellation;
        ExtensionInvocationResources? resources;
        ILogger? logger;
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            enumerator = _enumerator;
            _enumerator = null;
            bodyStream = _bodyStream;
            _bodyStream = null;
            streamCancellation = _streamCancellation;
            _streamCancellation = null;
            resources = _resources;
            _resources = null;
            logger = _logger;
            _logger = null;
        }

        bodyStream?.Clear();
        Exception? failure = null;
        try
        {
            if (streamCancellation is not null)
            {
                if (resources is not null)
                {
                    using IDisposable ambientContext = resources.EnterAmbientContext();
                    await streamCancellation.CancelAsync();
                }
                else
                {
                    await streamCancellation.CancelAsync();
                }
            }
        }
        catch (Exception exception)
        {
            failure = MapException(exception);
            LogFailure(logger, "stream cancellation", exception);
        }

        try
        {
            if (enumerator is not null)
            {
                await enumerator.DisposeAsync();
            }
        }
        catch (Exception exception)
        {
            failure ??= MapException(exception);
            LogFailure(logger, "stream enumerator disposal", exception);
        }
        finally
        {
            streamCancellation?.Dispose();
            if (resources is not null)
            {
                try
                {
                    await resources.DisposeAsync();
                }
                catch (Exception exception)
                {
                    failure ??= MapException(exception);
                    LogFailure(logger, "invocation resource disposal", exception);
                }
            }
        }

        if (failure is not null)
        {
            throw failure;
        }
    }

    private StreamEnumerator RegisterEnumerator(
        IAsyncEnumerator<ReadOnlyMemory<byte>> enumerator,
        CancellationTokenSource linkedCancellation,
        ExtensionInvocationResources resources
    )
    {
        lock (_gate)
        {
            if (_disposed || _enumerator is not null)
            {
                throw new AgwException(ErrorCodes.ExtensionUnavailable);
            }

            _enumerator = new StreamEnumerator(this, enumerator, linkedCancellation, resources);
            return _enumerator;
        }
    }

    private ExtensionInvocationResources GetResourcesForStream()
    {
        lock (_gate)
        {
            return !_disposed && _resources is { } resources
                ? resources
                : throw new AgwException(ErrorCodes.ExtensionUnavailable);
        }
    }

    private void ClearEnumerator(StreamEnumerator enumerator)
    {
        lock (_gate)
        {
            if (ReferenceEquals(_enumerator, enumerator))
            {
                _enumerator = null;
            }
        }
    }

    private CancellationToken CreateLinkedStreamToken()
    {
        lock (_gate)
        {
            if (_disposed || _streamCancellation is null)
            {
                throw new AgwException(ErrorCodes.ExtensionUnavailable);
            }

            return _streamCancellation.Token;
        }
    }

    internal static Exception MapException(Exception exception) =>
        exception is AgwException agwException
            ? new AgwException(agwException.Code, agwException.Message, agwException.StatusCode)
            : new AgwException(ErrorCodes.ExtensionUnavailable, "Extension response processing failed.");

    private void LogFailure(ILogger? logger, string phase, Exception exception) =>
        logger?.LogError(
            "Extension {ExtensionId} release {ReleaseHash} generation {GenerationId} {Phase} failed: {Failure}",
            _extensionId,
            _releaseHash,
            _generationId,
            phase,
            exception.ToString()
        );

    private sealed class BodyStreamAdapter : IAsyncEnumerable<ReadOnlyMemory<byte>>
    {
        private ExtensionInvocationResponse? _owner;
        private IAsyncEnumerable<ReadOnlyMemory<byte>>? _body;
        private int _enumerated;

        public BodyStreamAdapter(ExtensionInvocationResponse owner, IAsyncEnumerable<ReadOnlyMemory<byte>> body)
        {
            _owner = owner;
            _body = body;
        }

        public IAsyncEnumerator<ReadOnlyMemory<byte>> GetAsyncEnumerator(CancellationToken cancellationToken = default)
        {
            if (Interlocked.Exchange(ref _enumerated, 1) != 0)
            {
                throw new AgwException(
                    ErrorCodes.InvalidParam,
                    "Extension response stream can only be enumerated once."
                );
            }

            ExtensionInvocationResponse owner =
                Volatile.Read(ref _owner) ?? throw new AgwException(ErrorCodes.ExtensionUnavailable);
            IAsyncEnumerable<ReadOnlyMemory<byte>> body =
                Volatile.Read(ref _body) ?? throw new AgwException(ErrorCodes.ExtensionUnavailable);
            ExtensionInvocationResources resources = owner.GetResourcesForStream();
            CancellationTokenSource linkedCancellation = CancellationTokenSource.CreateLinkedTokenSource(
                cancellationToken,
                owner.CreateLinkedStreamToken()
            );
            try
            {
                using IDisposable ambientContext = resources.EnterAmbientContext();
                return owner.RegisterEnumerator(
                    body.GetAsyncEnumerator(linkedCancellation.Token),
                    linkedCancellation,
                    resources
                );
            }
            catch (Exception exception)
            {
                linkedCancellation.Dispose();
                throw MapException(exception);
            }
        }

        public void Clear()
        {
            Interlocked.Exchange(ref _body, null);
            Interlocked.Exchange(ref _owner, null);
        }
    }

    private sealed class StreamEnumerator : IAsyncEnumerator<ReadOnlyMemory<byte>>
    {
        private ExtensionInvocationResponse? _owner;
        private IAsyncEnumerator<ReadOnlyMemory<byte>>? _inner;
        private CancellationTokenSource? _linkedCancellation;
        private ExtensionInvocationResources? _resources;
        private int _disposed;

        public StreamEnumerator(
            ExtensionInvocationResponse owner,
            IAsyncEnumerator<ReadOnlyMemory<byte>> inner,
            CancellationTokenSource linkedCancellation,
            ExtensionInvocationResources resources
        )
        {
            _owner = owner;
            _inner = inner;
            _linkedCancellation = linkedCancellation;
            _resources = resources;
        }

        public ReadOnlyMemory<byte> Current
        {
            get
            {
                IAsyncEnumerator<ReadOnlyMemory<byte>> inner =
                    Volatile.Read(ref _inner) ?? throw new AgwException(ErrorCodes.ExtensionUnavailable);
                return inner.Current;
            }
        }

        public async ValueTask<bool> MoveNextAsync()
        {
            IAsyncEnumerator<ReadOnlyMemory<byte>> inner =
                Volatile.Read(ref _inner) ?? throw new AgwException(ErrorCodes.ExtensionUnavailable);
            ExtensionInvocationResources resources =
                Volatile.Read(ref _resources) ?? throw new AgwException(ErrorCodes.ExtensionUnavailable);
            CancellationToken linkedToken = Volatile.Read(ref _linkedCancellation)?.Token ?? default;
            using IDisposable ambientContext = resources.EnterAmbientContext();
            try
            {
                bool hasNext = await inner.MoveNextAsync();
                if (!hasNext)
                {
                    await DisposeAsync();
                }

                return hasNext;
            }
            catch (Exception exception)
            {
                ExtensionInvocationResponse? owner = Volatile.Read(ref _owner);
                await DisposeAsync();
                if (owner is not null)
                {
                    await owner.DisposeAsync();
                }

                if (exception is OperationCanceledException && linkedToken.IsCancellationRequested)
                {
                    linkedToken.ThrowIfCancellationRequested();
                }

                throw MapException(exception);
            }
        }

        public async ValueTask DisposeAsync()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0)
            {
                return;
            }

            IAsyncEnumerator<ReadOnlyMemory<byte>>? inner = Interlocked.Exchange(ref _inner, null);
            CancellationTokenSource? linkedCancellation = Interlocked.Exchange(ref _linkedCancellation, null);
            ExtensionInvocationResponse? owner = Interlocked.Exchange(ref _owner, null);
            ExtensionInvocationResources? resources = Volatile.Read(ref _resources);
            IDisposable? ambientContext = resources?.EnterAmbientContext();
            try
            {
                if (inner is not null)
                {
                    await inner.DisposeAsync();
                }
            }
            catch (Exception exception)
            {
                throw MapException(exception);
            }
            finally
            {
                ambientContext?.Dispose();
                linkedCancellation?.Dispose();
                owner?.ClearEnumerator(this);
                Interlocked.Exchange(ref _resources, null);
            }
        }
    }
}

internal sealed class ExtensionInvocationResources : IAsyncDisposable
{
    private readonly object _gate = new();
    private ExtensionGeneration? _generation;
    private AsyncServiceScope? _generationScope;
    private AsyncServiceScope? _hostScope;
    private string? _userId;
    private readonly Guid _invocationId;
    private readonly Guid _callerInvocationId;
    private readonly IExtensionCallerScope _callerContext;
    private ILogger? _logger;
    private Action? _releaseMetrics;
    private bool _disposed;

    public ExtensionInvocationResources(
        ExtensionGeneration generation,
        AsyncServiceScope generationScope,
        AsyncServiceScope hostScope,
        string? userId,
        Guid invocationId,
        Guid callerInvocationId,
        IExtensionCallerScope callerContext,
        ILogger logger,
        Action releaseMetrics
    )
    {
        _generation = generation;
        _generationScope = generationScope;
        _hostScope = hostScope;
        _userId = userId;
        _invocationId = invocationId;
        _callerInvocationId = callerInvocationId;
        _callerContext = callerContext;
        _logger = logger;
        _releaseMetrics = releaseMetrics;
    }

    public IDisposable EnterAmbientContext()
    {
        lock (_gate)
        {
            if (_disposed || _generation is not { } generation || _hostScope is not { } hostScope)
            {
                throw new AgwException(ErrorCodes.ExtensionUnavailable);
            }

            IDisposable? callerContextScope = null;
            IDisposable? capabilitiesScope = null;
            IDisposable? userContextScope = null;
            IDisposable? hostUserContextScope = null;
            try
            {
                callerContextScope = _callerContext.Push(
                    new ExtensionCaller
                    {
                        ExtensionId = generation.GenerationId.ExtensionId,
                        GenerationId = generation.GenerationId.GenerationId,
                        UserId = _userId,
                        InvocationId = _callerInvocationId,
                    }
                );
                capabilitiesScope = generation.HostCapabilities.Push(hostScope.ServiceProvider);
                userContextScope = generation.UserContext?.Push(_userId, _invocationId);
                hostUserContextScope = PushHostUserContext(_userId);
                return new AmbientContextScope(
                    callerContextScope,
                    capabilitiesScope,
                    userContextScope,
                    hostUserContextScope
                );
            }
            catch
            {
                hostUserContextScope?.Dispose();
                userContextScope?.Dispose();
                capabilitiesScope?.Dispose();
                callerContextScope?.Dispose();
                throw;
            }
        }
    }

    public async ValueTask DisposeAsync()
    {
        ExtensionGeneration? generation;
        AsyncServiceScope? generationScope;
        AsyncServiceScope? hostScope;
        string? userId;
        Guid invocationId;
        Guid callerInvocationId;
        ILogger? logger;
        Action? releaseMetrics;
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            generation = _generation;
            _generation = null;
            generationScope = _generationScope;
            _generationScope = null;
            hostScope = _hostScope;
            _hostScope = null;
            userId = _userId;
            _userId = null;
            invocationId = _invocationId;
            callerInvocationId = _callerInvocationId;
            logger = _logger;
            _logger = null;
            releaseMetrics = _releaseMetrics;
            _releaseMetrics = null;
        }

        IDisposable? hostCapabilitiesScope = null;
        IDisposable? userContextScope = null;
        IDisposable? callerContextScope = null;
        IDisposable? hostUserContextScope = null;
        if (generation is not null && hostScope is { } activeHostScope)
        {
            callerContextScope = _callerContext.Push(
                new ExtensionCaller
                {
                    ExtensionId = generation.GenerationId.ExtensionId,
                    GenerationId = generation.GenerationId.GenerationId,
                    UserId = userId,
                    InvocationId = callerInvocationId,
                }
            );
            hostCapabilitiesScope = generation.HostCapabilities.Push(activeHostScope.ServiceProvider);
            userContextScope = generation.UserContext?.Push(userId, invocationId);
            hostUserContextScope = PushHostUserContext(userId);
        }

        Exception? failure = null;

        try
        {
            if (generationScope is { } scope)
            {
                await scope.DisposeAsync();
            }
        }
        catch (Exception exception)
        {
            failure = ExtensionInvocationResponse.MapException(exception);
            logger?.LogError(
                "Extension {ExtensionId} release {ReleaseHash} generation {GenerationId} generation scope disposal failed: {Failure}",
                generation?.GenerationId.ExtensionId,
                generation?.Request.ReleaseHash,
                generation?.GenerationId.GenerationId,
                exception.ToString()
            );
        }

        try
        {
            if (hostScope is { } scope)
            {
                await scope.DisposeAsync();
            }
        }
        catch (Exception exception)
        {
            failure ??= ExtensionInvocationResponse.MapException(exception);
            logger?.LogError(
                "Extension {ExtensionId} release {ReleaseHash} generation {GenerationId} host scope disposal failed: {Failure}",
                generation?.GenerationId.ExtensionId,
                generation?.Request.ReleaseHash,
                generation?.GenerationId.GenerationId,
                exception.ToString()
            );
        }
        finally
        {
            hostUserContextScope?.Dispose();
            userContextScope?.Dispose();
            hostCapabilitiesScope?.Dispose();
            callerContextScope?.Dispose();
            generation?.FinishInvocation(invocationId);
            releaseMetrics?.Invoke();
        }

        if (failure is not null)
        {
            throw failure;
        }
    }

    private static IDisposable PushHostUserContext(string? userId) =>
        UserInfoUtil.Push(
            userId is null
                ? null
                : new ClaimsPrincipal(
                    new ClaimsIdentity([new Claim(ClaimTypes.NameIdentifier, userId)], "ExtensionInvocation")
                )
        );

    private sealed class AmbientContextScope : IDisposable
    {
        private IDisposable? _callerContextScope;
        private IDisposable? _capabilitiesScope;
        private IDisposable? _userContextScope;
        private IDisposable? _hostUserContextScope;

        public AmbientContextScope(
            IDisposable callerContextScope,
            IDisposable capabilitiesScope,
            IDisposable? userContextScope,
            IDisposable hostUserContextScope
        )
        {
            _callerContextScope = callerContextScope;
            _capabilitiesScope = capabilitiesScope;
            _userContextScope = userContextScope;
            _hostUserContextScope = hostUserContextScope;
        }

        public void Dispose()
        {
            Interlocked.Exchange(ref _hostUserContextScope, null)?.Dispose();
            Interlocked.Exchange(ref _userContextScope, null)?.Dispose();
            Interlocked.Exchange(ref _capabilitiesScope, null)?.Dispose();
            Interlocked.Exchange(ref _callerContextScope, null)?.Dispose();
        }
    }
}
