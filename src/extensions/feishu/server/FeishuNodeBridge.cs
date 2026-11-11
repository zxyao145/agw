using System.Collections.Concurrent;
using System.Diagnostics;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization.Metadata;
using Agw.Extensions.Contracts;
using Agw.Shared.Exceptions;

namespace Agw.Extensions.Feishu;

public sealed class FeishuNodeBridge : IAsyncDisposable
{
    private readonly SemaphoreSlim _apiGate = new(1, 1);
    private readonly object _responseLock = new();
    private Process? _apiProcess;
    private Task? _apiOutputPump;
    private TaskCompletionSource _apiReady = NewReadySource();
    private TaskCompletionSource<FeishuNodeWorkerResponse>? _apiResponse;
    private string? _apiRequestId;

    public async Task<FeishuNodeVerification> VerifyAsync(
        FeishuNodeApplication app,
        CancellationToken cancellationToken
    ) =>
        await RunAsync(
            "verify",
            new JsonObject { ["app"] = ToJsonNode(app, FeishuNodeJsonContext.Default.FeishuNodeApplication) },
            FeishuNodeJsonContext.Default.FeishuNodeVerification,
            cancellationToken
        );

    public async Task<IReadOnlyList<FeishuNodeChat>> DiscoverChatsAsync(
        FeishuNodeApplication app,
        CancellationToken cancellationToken
    ) =>
        await RunAsync(
            "discover",
            new JsonObject { ["app"] = ToJsonNode(app, FeishuNodeJsonContext.Default.FeishuNodeApplication) },
            FeishuNodeJsonContext.Default.FeishuNodeChatArray,
            cancellationToken
        );

    public async Task<FeishuNodeMessage> SendApplicationMessageAsync(
        FeishuNodeApplication app,
        FeishuTargetKind kind,
        string receiveId,
        NotificationMessage message,
        CancellationToken cancellationToken
    ) =>
        await RunAsync(
            "send",
            new JsonObject
            {
                ["app"] = ToJsonNode(app, FeishuNodeJsonContext.Default.FeishuNodeApplication),
                ["receiveId"] = receiveId,
                ["receiveIdType"] = kind == FeishuTargetKind.Chat ? "chat_id" : "open_id",
                ["idempotencyKey"] = message.IdempotencyKey,
                ["content"] = ToJsonNode(FeishuCards.NotificationCard(message, message.Link, interactive: true)),
            },
            FeishuNodeJsonContext.Default.FeishuNodeMessage,
            cancellationToken
        );

    public async Task UpdateApplicationMessageAsync(
        FeishuNodeApplication app,
        string messageId,
        NotificationMessage message,
        CancellationToken cancellationToken
    ) =>
        await RunAsync(
            "update",
            new JsonObject
            {
                ["app"] = ToJsonNode(app, FeishuNodeJsonContext.Default.FeishuNodeApplication),
                ["messageId"] = messageId,
                ["content"] = ToJsonNode(FeishuCards.NotificationCard(message, message.Link, interactive: true)),
            },
            FeishuNodeJsonContext.Default.JsonElement,
            cancellationToken
        );

    public async Task UpdateApplicationCardAsync(
        FeishuNodeApplication app,
        string messageId,
        JsonElement content,
        CancellationToken cancellationToken
    ) =>
        await RunAsync(
            "update",
            new JsonObject
            {
                ["app"] = ToJsonNode(app, FeishuNodeJsonContext.Default.FeishuNodeApplication),
                ["messageId"] = messageId,
                ["content"] = ToJsonNode(content),
            },
            FeishuNodeJsonContext.Default.JsonElement,
            cancellationToken
        );

    public async Task<FeishuNodeMessage> SendWebhookMessageAsync(
        FeishuNodeWebhookRequest request,
        CancellationToken cancellationToken
    ) =>
        await RunAsync(
            "webhook",
            ToJsonNode(request, FeishuNodeJsonContext.Default.FeishuNodeWebhookRequest).AsObject(),
            FeishuNodeJsonContext.Default.FeishuNodeMessage,
            cancellationToken
        );

    public async Task<FeishuNodeMessage> SendTextAsync(
        FeishuNodeApplication app,
        FeishuTargetKind kind,
        string receiveId,
        string text,
        string idempotencyKey,
        CancellationToken cancellationToken
    ) =>
        await RunAsync(
            "sendText",
            new JsonObject
            {
                ["app"] = ToJsonNode(app, FeishuNodeJsonContext.Default.FeishuNodeApplication),
                ["receiveId"] = receiveId,
                ["receiveIdType"] = kind == FeishuTargetKind.Chat ? "chat_id" : "open_id",
                ["text"] = text,
                ["idempotencyKey"] = idempotencyKey,
            },
            FeishuNodeJsonContext.Default.FeishuNodeMessage,
            cancellationToken
        );

    public async Task<FeishuNodeMessage> SendApplicationCardAsync(
        FeishuNodeApplication app,
        FeishuTargetKind kind,
        string receiveId,
        JsonElement card,
        string idempotencyKey,
        CancellationToken cancellationToken
    ) =>
        await RunAsync(
            "sendCard",
            new JsonObject
            {
                ["app"] = ToJsonNode(app, FeishuNodeJsonContext.Default.FeishuNodeApplication),
                ["receiveId"] = receiveId,
                ["receiveIdType"] = kind == FeishuTargetKind.Chat ? "chat_id" : "open_id",
                ["content"] = ToJsonNode(card),
                ["idempotencyKey"] = idempotencyKey,
            },
            FeishuNodeJsonContext.Default.FeishuNodeMessage,
            cancellationToken
        );

    public async Task<FeishuNodeConnection> StartWebSocketAsync(
        Guid appRecordId,
        FeishuNodeApplication app,
        Func<FeishuNodeEvent, CancellationToken, ValueTask> eventHandler,
        Func<FeishuNodeEvent, CancellationToken, ValueTask<JsonElement>> callbackHandler,
        Action<string> statusHandler,
        CancellationToken cancellationToken
    )
    {
        string workerPath = GetWorkerPath();
        var process = new Process { StartInfo = CreateStartInfo(workerPath) };
        if (!process.Start())
            throw new AgwException(ErrorCodes.ExtensionUnavailable, "The Feishu Node worker could not be started.");
        var connection = new FeishuNodeConnection(process, eventHandler, callbackHandler, statusHandler);
        connection.BeginOutputPump(cancellationToken);
        try
        {
            await process.StandardInput.WriteLineAsync(
                new JsonObject
                {
                    ["operation"] = "startWs",
                    ["appRecordId"] = appRecordId,
                    ["app"] = ToJsonNode(app, FeishuNodeJsonContext.Default.FeishuNodeApplication),
                }.ToJsonString(FeishuNodeJsonContext.Default.Options)
            );
            await process.StandardInput.FlushAsync(cancellationToken);
            await connection.Ready.WaitAsync(TimeSpan.FromSeconds(15), cancellationToken);
            return connection;
        }
        catch
        {
            await connection.DisposeAsync();
            throw;
        }
    }

    private async Task<T> RunAsync<T>(
        string operation,
        JsonObject payload,
        JsonTypeInfo<T> resultType,
        CancellationToken cancellationToken
    )
    {
        await _apiGate.WaitAsync(cancellationToken);
        try
        {
            await EnsureApiWorkerAsync(cancellationToken);
            string requestId = Guid.CreateVersion7().ToString("N");
            var responseSource = new TaskCompletionSource<FeishuNodeWorkerResponse>(
                TaskCreationOptions.RunContinuationsAsynchronously
            );
            lock (_responseLock)
            {
                _apiRequestId = requestId;
                _apiResponse = responseSource;
            }

            try
            {
                Process process =
                    _apiProcess
                    ?? throw new AgwException(ErrorCodes.ExtensionUnavailable, "The Feishu Node worker stopped.");
                await process.StandardInput.WriteLineAsync(SerializeCommand(operation, payload, requestId));
                await process.StandardInput.FlushAsync(cancellationToken);
                FeishuNodeWorkerResponse response = await responseSource.Task.WaitAsync(cancellationToken);
                if (!response.Ok)
                    throw new AgwException(ErrorCodes.ExtensionUnavailable, "The Feishu Node worker operation failed.");
                return response.Payload.Deserialize(resultType)
                    ?? throw new AgwException(
                        ErrorCodes.ExtensionUnavailable,
                        "The Feishu Node worker result is invalid."
                    );
            }
            finally
            {
                lock (_responseLock)
                {
                    if (_apiRequestId == requestId)
                    {
                        _apiRequestId = null;
                        _apiResponse = null;
                    }
                }
            }
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (AgwException)
        {
            throw;
        }
        catch (Exception exception) when (exception is IOException or JsonException or InvalidOperationException)
        {
            throw new AgwException(
                ErrorCodes.ExtensionUnavailable,
                "The Feishu Node worker operation failed.",
                exception
            );
        }
        finally
        {
            _apiGate.Release();
        }
    }

    private static async Task<T> RunOneShotAsync<T>(
        string operation,
        JsonObject payload,
        JsonTypeInfo<T> resultType,
        CancellationToken cancellationToken
    )
    {
        using var process = new Process { StartInfo = CreateStartInfo(GetWorkerPath()) };
        if (!process.Start())
            throw new AgwException(ErrorCodes.ExtensionUnavailable, "The Feishu Node worker could not be started.");
        using CancellationTokenRegistration cancellation = cancellationToken.Register(() =>
        {
            if (!process.HasExited)
                process.Kill(entireProcessTree: true);
        });
        try
        {
            await process.StandardInput.WriteLineAsync(SerializeCommand(operation, payload));
            await process.StandardInput.FlushAsync(cancellationToken);
            process.StandardInput.Close();
            string? responseLine = await process.StandardOutput.ReadLineAsync(cancellationToken);
            await process.WaitForExitAsync(cancellationToken);
            if (string.IsNullOrWhiteSpace(responseLine))
                throw new AgwException(ErrorCodes.ExtensionUnavailable, "The Feishu Node worker returned no result.");
            FeishuNodeWorkerResponse? response = JsonSerializer.Deserialize(
                responseLine,
                FeishuNodeJsonContext.Default.FeishuNodeWorkerResponse
            );
            if (response is null || !response.Ok)
                throw new AgwException(ErrorCodes.ExtensionUnavailable, "The Feishu Node worker operation failed.");
            return response.Payload.Deserialize(resultType)
                ?? throw new AgwException(ErrorCodes.ExtensionUnavailable, "The Feishu Node worker result is invalid.");
        }
        catch (OperationCanceledException)
        {
            if (!process.HasExited)
                process.Kill(entireProcessTree: true);
            throw;
        }
        catch (AgwException)
        {
            throw;
        }
        catch (Exception exception) when (exception is IOException or JsonException or InvalidOperationException)
        {
            throw new AgwException(
                ErrorCodes.ExtensionUnavailable,
                "The Feishu Node worker operation failed.",
                exception
            );
        }
    }

    internal static string SerializeCommand(string operation, JsonObject payload, string? requestId = null)
    {
        var command = new JsonObject { ["operation"] = operation };
        if (requestId is not null)
            command["requestId"] = requestId;
        command["payload"] = payload;
        return command.ToJsonString(FeishuNodeJsonContext.Default.Options);
    }

    private static JsonNode ToJsonNode(JsonElement element) =>
        JsonNode.Parse(element.GetRawText())
        ?? throw new AgwException(ErrorCodes.ExtensionUnavailable, "The Feishu Node worker payload is invalid.");

    private static JsonNode ToJsonNode<T>(T value, JsonTypeInfo<T> typeInfo) =>
        JsonSerializer.SerializeToNode(value, typeInfo)
        ?? throw new AgwException(ErrorCodes.ExtensionUnavailable, "The Feishu Node worker payload is invalid.");

    private async Task EnsureApiWorkerAsync(CancellationToken cancellationToken)
    {
        if (_apiProcess is { HasExited: false })
        {
            await _apiReady.Task.WaitAsync(TimeSpan.FromSeconds(15), cancellationToken);
            return;
        }

        if (_apiProcess is { } stopped)
        {
            if (_apiOutputPump is not null)
                await _apiOutputPump;
            stopped.Dispose();
        }

        _apiReady = NewReadySource();
        Process process = new() { StartInfo = CreateStartInfo(GetWorkerPath()) };
        if (!process.Start())
        {
            process.Dispose();
            throw new AgwException(ErrorCodes.ExtensionUnavailable, "The Feishu Node worker could not be started.");
        }
        _apiProcess = process;
        _apiOutputPump = ReadApiOutputAsync(process);
        await process.StandardInput.WriteLineAsync(
            new JsonObject { ["operation"] = "startDaemon" }.ToJsonString(FeishuNodeJsonContext.Default.Options)
        );
        await process.StandardInput.FlushAsync(cancellationToken);
        await _apiReady.Task.WaitAsync(TimeSpan.FromSeconds(15), cancellationToken);
    }

    private async Task ReadApiOutputAsync(Process process)
    {
        try
        {
            while (await process.StandardOutput.ReadLineAsync() is { } line)
            {
                using JsonDocument document = JsonDocument.Parse(line);
                JsonElement root = document.RootElement;
                if (!root.TryGetProperty("type", out JsonElement type))
                    continue;
                switch (type.GetString())
                {
                    case "ready":
                        _apiReady.TrySetResult();
                        break;
                    case "result":
                        string? requestId = root.TryGetProperty("requestId", out JsonElement idValue)
                            ? idValue.GetString()
                            : null;
                        FeishuNodeWorkerResponse? response = root.Deserialize(
                            FeishuNodeJsonContext.Default.FeishuNodeWorkerResponse
                        );
                        lock (_responseLock)
                        {
                            if (requestId is not null && requestId == _apiRequestId && response is not null)
                                _apiResponse?.TrySetResult(response);
                        }
                        break;
                    case "error":
                        FailApiWorker(
                            new AgwException(ErrorCodes.ExtensionUnavailable, "The Feishu Node worker failed.")
                        );
                        return;
                }
            }

            FailApiWorker(new AgwException(ErrorCodes.ExtensionUnavailable, "The Feishu Node worker stopped."));
        }
        catch (Exception exception) when (exception is IOException or JsonException or AgwException)
        {
            FailApiWorker(exception);
        }
    }

    private void FailApiWorker(Exception exception)
    {
        _apiReady.TrySetException(exception);
        lock (_responseLock)
            _apiResponse?.TrySetException(exception);
    }

    public async ValueTask DisposeAsync()
    {
        await _apiGate.WaitAsync();
        try
        {
            Process? process = _apiProcess;
            _apiProcess = null;
            if (process is null)
                return;
            if (!process.HasExited)
                process.Kill(entireProcessTree: true);
            await process.WaitForExitAsync(CancellationToken.None);
            if (_apiOutputPump is not null)
                await _apiOutputPump;
            process.Dispose();
            _apiOutputPump = null;
        }
        finally
        {
            _apiGate.Release();
            _apiGate.Dispose();
        }
    }

    private static TaskCompletionSource NewReadySource() => new(TaskCreationOptions.RunContinuationsAsynchronously);

    private static ProcessStartInfo CreateStartInfo(string workerPath)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = "node",
            UseShellExecute = false,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = false,
            WorkingDirectory = Path.GetDirectoryName(workerPath)!,
        };
        startInfo.ArgumentList.Add(workerPath);
        return startInfo;
    }

    private static string GetWorkerPath()
    {
        string assemblyPath = Assembly.GetExecutingAssembly().Location;
        string? directory = Path.GetDirectoryName(assemblyPath);
        string workerPath = Path.Combine(directory ?? AppContext.BaseDirectory, "feishu-worker.mjs");
        return File.Exists(workerPath)
            ? workerPath
            : throw new AgwException(ErrorCodes.ExtensionUnavailable, "The Feishu Node worker asset is missing.");
    }
}

public sealed class FeishuNodeConnection : IAsyncDisposable
{
    private readonly Process _process;
    private readonly Func<FeishuNodeEvent, CancellationToken, ValueTask> _eventHandler;
    private readonly Func<FeishuNodeEvent, CancellationToken, ValueTask<JsonElement>> _callbackHandler;
    private readonly Action<string> _statusHandler;
    private readonly object _callbackCancellationLock = new();
    private readonly ConcurrentDictionary<string, CancellationTokenSource> _callbackCancellations = new(
        StringComparer.Ordinal
    );
    private readonly ConcurrentDictionary<long, Task> _handlers = new();
    private readonly TaskCompletionSource<Exception> _handlerFailure = new(
        TaskCreationOptions.RunContinuationsAsynchronously
    );
    private readonly SemaphoreSlim _inputWriteGate = new(1, 1);
    private readonly TaskCompletionSource _ready = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private CancellationTokenSource? _lifetime;
    private Task? _outputPump;
    private long _nextHandlerId;

    internal FeishuNodeConnection(
        Process process,
        Func<FeishuNodeEvent, CancellationToken, ValueTask> eventHandler,
        Func<FeishuNodeEvent, CancellationToken, ValueTask<JsonElement>> callbackHandler,
        Action<string> statusHandler
    )
    {
        _process = process;
        _eventHandler = eventHandler;
        _callbackHandler = callbackHandler;
        _statusHandler = statusHandler;
    }

    public Task Ready => _ready.Task;

    public Task Completion => _outputPump ?? Task.CompletedTask;

    internal void BeginOutputPump(CancellationToken cancellationToken)
    {
        _lifetime = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        _outputPump = ReadOutputAsync(_lifetime.Token);
    }

    public async ValueTask DisposeAsync()
    {
        _lifetime?.Cancel();
        if (!_process.HasExited)
            _process.Kill(entireProcessTree: true);
        await _process.WaitForExitAsync(CancellationToken.None);
        if (_outputPump is not null)
            await _outputPump;
        _lifetime?.Dispose();
        _process.Dispose();
    }

    private async Task ReadOutputAsync(CancellationToken cancellationToken)
    {
        try
        {
            while (true)
            {
                Task<string?> readLine = _process.StandardOutput.ReadLineAsync(cancellationToken).AsTask();
                Task completed = await Task.WhenAny(readLine, _handlerFailure.Task);
                if (completed == _handlerFailure.Task)
                    throw await _handlerFailure.Task;
                if (await readLine is not { } line)
                    break;

                using JsonDocument document = JsonDocument.Parse(line);
                JsonElement root = document.RootElement;
                if (!root.TryGetProperty("type", out JsonElement type))
                    continue;
                switch (type.GetString())
                {
                    case "ready":
                        _ready.TrySetResult();
                        break;
                    case "event":
                        FeishuNodeEvent? notification = root.Deserialize(FeishuNodeJsonContext.Default.FeishuNodeEvent);
                        if (notification is not null)
                            Dispatch(token =>
                                _eventHandler(notification with { Payload = notification.Payload.Clone() }, token)
                                    .AsTask()
                            );
                        break;
                    case "callback":
                        FeishuNodeEvent callback =
                            root.Deserialize(FeishuNodeJsonContext.Default.FeishuNodeEvent)
                            ?? throw new JsonException("The Feishu Node worker callback is invalid.");
                        if (string.IsNullOrWhiteSpace(callback.RequestId))
                            throw new JsonException("The Feishu Node worker callback request id is invalid.");
                        callback = callback with { Payload = callback.Payload.Clone() };
                        var callbackCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                        lock (_callbackCancellationLock)
                        {
                            if (!_callbackCancellations.TryAdd(callback.RequestId, callbackCancellation))
                            {
                                callbackCancellation.Dispose();
                                throw new JsonException("The Feishu Node worker callback request id is duplicated.");
                            }
                        }
                        Dispatch(async token =>
                        {
                            try
                            {
                                JsonElement response = await _callbackHandler(callback, callbackCancellation.Token);
                                if (callbackCancellation.IsCancellationRequested || token.IsCancellationRequested)
                                    return;
                                var result = new JsonObject
                                {
                                    ["type"] = "callbackResult",
                                    ["requestId"] = callback.RequestId,
                                    ["payload"] = JsonNode.Parse(response.GetRawText()),
                                };
                                await _inputWriteGate.WaitAsync(token);
                                try
                                {
                                    await _process.StandardInput.WriteLineAsync(
                                        result.ToJsonString(FeishuNodeJsonContext.Default.Options)
                                    );
                                    await _process.StandardInput.FlushAsync(token);
                                }
                                finally
                                {
                                    _inputWriteGate.Release();
                                }
                            }
                            finally
                            {
                                lock (_callbackCancellationLock)
                                {
                                    _callbackCancellations.TryRemove(callback.RequestId, out _);
                                    callbackCancellation.Dispose();
                                }
                            }
                        });
                        break;
                    case "callbackCancel":
                        if (
                            !root.TryGetProperty("requestId", out JsonElement cancelId)
                            || cancelId.ValueKind != JsonValueKind.String
                            || string.IsNullOrWhiteSpace(cancelId.GetString())
                        )
                            throw new JsonException("The Feishu Node worker callback cancellation is invalid.");
                        lock (_callbackCancellationLock)
                        {
                            if (
                                _callbackCancellations.TryGetValue(
                                    cancelId.GetString()!,
                                    out CancellationTokenSource? pending
                                )
                            )
                                pending.Cancel();
                        }
                        break;
                    case "status":
                        if (!root.TryGetProperty("state", out JsonElement state) || state.GetString() is not { } value)
                            throw new JsonException("The Feishu Node worker status is invalid.");
                        _statusHandler(value);
                        break;
                    case "error":
                        throw new AgwException(ErrorCodes.ExtensionUnavailable, "The Feishu connection failed.");
                }
            }
            if (!_ready.Task.IsCompleted)
                _ready.TrySetException(
                    new AgwException(ErrorCodes.ExtensionUnavailable, "The Feishu connection stopped before startup.")
                );
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            _ready.TrySetCanceled(cancellationToken);
        }
        catch (IOException) when (cancellationToken.IsCancellationRequested)
        {
            _ready.TrySetCanceled(cancellationToken);
        }
        catch (Exception exception) when (exception is IOException or JsonException or AgwException)
        {
            _ready.TrySetException(exception);
            throw;
        }
        finally
        {
            _lifetime?.Cancel();
            Task[] handlers = _handlers.Values.ToArray();
            try
            {
                await Task.WhenAll(handlers);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                _ready.TrySetCanceled(cancellationToken);
            }
        }
    }

    private void Dispatch(Func<CancellationToken, Task> handler)
    {
        long handlerId = Interlocked.Increment(ref _nextHandlerId);
        CancellationToken cancellationToken = _lifetime?.Token ?? CancellationToken.None;
        Task task = Task.Run(() => handler(cancellationToken), CancellationToken.None);
        _handlers[handlerId] = task;
        _ = task.ContinueWith(
            completed =>
            {
                _handlers.TryRemove(handlerId, out _);
                if (completed.IsFaulted)
                    _handlerFailure.TrySetResult(completed.Exception!.GetBaseException());
            },
            CancellationToken.None,
            TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default
        );
    }
}
