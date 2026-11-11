using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Agw.Extensions.Contracts;
using Agw.Extensions.Sdk;
using Agw.Extensions.Sdk.EntityFrameworkCore;
using Agw.Shared.Exceptions;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Agw.Extensions.Feishu;

public sealed class FeishuBackgroundService : IExtensionBackgroundService
{
    private static readonly TimeSpan ScanInterval = TimeSpan.FromSeconds(20);
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly FeishuNodeBridge _nodeBridge;
    private readonly FeishuRuntimeState _runtimeState;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<FeishuBackgroundService> _logger;
    private readonly ConcurrentDictionary<Guid, ManagedConnection> _connections = new();
    private ExtensionOwnedRecordScheduler<FeishuDbContext, FeishuApp>? _apps;
    private IExtensionContext? _context;
    private CancellationTokenSource? _runCancellation;
    private Task? _runTask;

    public FeishuBackgroundService(
        IServiceScopeFactory scopeFactory,
        FeishuNodeBridge nodeBridge,
        FeishuRuntimeState runtimeState,
        TimeProvider timeProvider,
        ILogger<FeishuBackgroundService> logger
    )
    {
        _scopeFactory = scopeFactory;
        _nodeBridge = nodeBridge;
        _runtimeState = runtimeState;
        _timeProvider = timeProvider;
        _logger = logger;
    }

    public ValueTask StartAsync(
        IExtensionContext context,
        IExtensionBackgroundScheduler scheduler,
        CancellationToken cancellationToken
    )
    {
        _context = context;
        _apps = new ExtensionOwnedRecordScheduler<FeishuDbContext, FeishuApp>(_scopeFactory, scheduler);
        StartLoop(cancellationToken);
        _runtimeState.RequestScan();
        return ValueTask.CompletedTask;
    }

    public async ValueTask PauseAsync(CancellationToken cancellationToken)
    {
        await StopLoopAsync(cancellationToken);
    }

    public ValueTask ResumeAsync(CancellationToken cancellationToken)
    {
        StartLoop(cancellationToken);
        _runtimeState.RequestScan();
        return ValueTask.CompletedTask;
    }

    public async ValueTask StopAsync(CancellationToken cancellationToken)
    {
        await StopLoopAsync(cancellationToken);
        _context = null;
        _apps = null;
    }

    private void StartLoop(CancellationToken cancellationToken)
    {
        if (_runTask is not null)
            return;
        _runCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        _runTask = RunAsync(_runCancellation.Token);
    }

    private async Task StopLoopAsync(CancellationToken cancellationToken)
    {
        if (_runCancellation is null || _runTask is null)
            return;
        _runCancellation.Cancel();
        try
        {
            await _runTask;
        }
        finally
        {
            _runCancellation.Dispose();
            _runCancellation = null;
            _runTask = null;
        }
        cancellationToken.ThrowIfCancellationRequested();
    }

    private async Task RunAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                await ReconcileAsync(cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception exception)
            {
                _logger.LogError(
                    exception,
                    "Feishu application scan failed for extension {ExtensionId}",
                    _context?.ExtensionId
                );
            }

            try
            {
                await WaitForScanOrIntervalAsync(cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                break;
            }
        }

        await StopConnectionsAsync();
    }

    private async Task ReconcileAsync(CancellationToken cancellationToken)
    {
        ExtensionOwnedRecordScheduler<FeishuDbContext, FeishuApp> scheduler =
            _apps ?? throw new AgwException(ErrorCodes.ExtensionUnavailable);
        Guid[] appIds = (await scheduler.ListIdsAsync(cancellationToken)).ToArray();
        var desired = new Dictionary<Guid, AppConnectionSettings>();
        foreach (Guid appId in appIds)
        {
            AppConnectionSettings? settings = await scheduler.ExecuteAsync(
                appId,
                (db, app, context, token) =>
                    ValueTask.FromResult(
                        app.Mode != FeishuAppMode.App || !app.Enabled
                            ? null
                            : new AppConnectionSettings(app.Id, app.Version, ToNodeApplication(context, app))
                    ),
                cancellationToken
            );
            if (settings is not null)
                desired.Add(appId, settings);
        }

        foreach ((Guid appId, ManagedConnection connection) in _connections.ToArray())
        {
            if (
                !desired.TryGetValue(appId, out AppConnectionSettings? settings)
                || settings.Version != connection.Version
            )
            {
                connection.Cancellation.Cancel();
                await connection.Task.WaitAsync(cancellationToken);
            }
        }

        foreach ((Guid appId, AppConnectionSettings settings) in desired)
        {
            if (_connections.ContainsKey(appId))
                continue;
            var managed = new ManagedConnection(
                settings.Version,
                CancellationTokenSource.CreateLinkedTokenSource(cancellationToken)
            );
            if (!_connections.TryAdd(appId, managed))
            {
                managed.Cancellation.Dispose();
                managed.CallbackGate.Dispose();
                continue;
            }
            _runtimeState.RegisterConnection(appId, managed.Cancellation);
            managed.Task = RunConnectionAsync(appId, settings, managed.CallbackGate, managed.Cancellation.Token);
        }
    }

    private async Task RunConnectionAsync(
        Guid appId,
        AppConnectionSettings settings,
        SemaphoreSlim callbackGate,
        CancellationToken cancellationToken
    )
    {
        _runtimeState.SetStatus(appId, "Connecting");
        try
        {
            IExtensionContext context = _context ?? throw new AgwException(ErrorCodes.ExtensionUnavailable);
            await using IExtensionLease lease = await context.Leases.AcquireAsync($"ws:{appId:D}", cancellationToken);
            AppConnectionSettings? current = await (
                _apps ?? throw new AgwException(ErrorCodes.ExtensionUnavailable)
            ).ExecuteAsync(
                appId,
                (db, app, ownerContext, token) =>
                    ValueTask.FromResult(
                        app.Mode != FeishuAppMode.App || !app.Enabled
                            ? null
                            : new AppConnectionSettings(app.Id, app.Version, ToNodeApplication(ownerContext, app))
                    ),
                cancellationToken
            );
            if (current is null || current.Version != settings.Version)
                return;

            await using FeishuNodeConnection connection = await _nodeBridge.StartWebSocketAsync(
                appId,
                settings.Application,
                (notification, token) => ProcessEventAsync(appId, notification, token),
                (notification, token) => ProcessCardActionAsync(appId, callbackGate, notification, token),
                state =>
                {
                    string status = state switch
                    {
                        "idle" or "connecting" => "Connecting",
                        "connected" => "Connected",
                        "reconnecting" => "Reconnecting",
                        "failed" => "Failed",
                        _ => throw new AgwException(ErrorCodes.ExtensionPackageInvalid),
                    };
                    _runtimeState.SetStatus(
                        appId,
                        status,
                        status == "Failed" ? "The Feishu connection could not be established." : null
                    );
                },
                cancellationToken
            );
            Task leaseLost = Task.Delay(Timeout.InfiniteTimeSpan, lease.HandleLostToken);
            Task stopRequested = Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            Task completed = await Task.WhenAny(leaseLost, stopRequested, connection.Completion);
            if (completed == connection.Completion)
            {
                await connection.Completion;
                if (!cancellationToken.IsCancellationRequested && !lease.HandleLostToken.IsCancellationRequested)
                    throw new AgwException(ErrorCodes.ExtensionUnavailable, "The Feishu connection ended.");
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            _runtimeState.SetStatus(appId, "Disconnected");
        }
        catch (OperationCanceledException) when (_context is not null)
        {
            _runtimeState.SetStatus(appId, "Connecting");
        }
        catch (Exception exception)
        {
            _runtimeState.SetStatus(appId, "Failed", "The Feishu connection could not be established.");
            _logger.LogError(exception, "Feishu connection failed for application {AppId}", appId);
        }
        finally
        {
            if (_connections.TryGetValue(appId, out ManagedConnection? connection))
            {
                _runtimeState.RemoveConnection(appId, connection.Cancellation, retainFailure: true);
                _connections.TryRemove(new KeyValuePair<Guid, ManagedConnection>(appId, connection));
                connection.Cancellation.Dispose();
                connection.CallbackGate.Dispose();
            }
        }
    }

    private async ValueTask ProcessEventAsync(
        Guid appId,
        FeishuNodeEvent notification,
        CancellationToken cancellationToken
    )
    {
        if (
            notification.EventKey != "im.message.receive_v1"
            || !TryReadMessage(notification.Payload, out FeishuEventMessage message)
        )
            return;
        string trimmed = message.Text.Trim();
        if (
            !trimmed.StartsWith("/bind", StringComparison.OrdinalIgnoreCase)
            && !trimmed.StartsWith("bind", StringComparison.OrdinalIgnoreCase)
        )
            return;

        string[] command = trimmed.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        string? code = command.Length > 1 ? command[1] : null;
        await (_apps ?? throw new AgwException(ErrorCodes.ExtensionUnavailable)).ExecuteAsync(
            appId,
            async (db, app, context, token) =>
            {
                await ProcessBindingMessageAsync(db, app, context, message, code, token);
                return true;
            },
            cancellationToken
        );
    }

    private async ValueTask<JsonElement> ProcessCardActionAsync(
        Guid appId,
        SemaphoreSlim callbackGate,
        FeishuNodeEvent notification,
        CancellationToken cancellationToken
    )
    {
        if (notification.EventKey != "card.action.trigger")
            return SerializeCallbackAcknowledgement(CallbackUnavailable());

        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(2), _timeProvider);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeout.Token);
        bool callbackGateAcquired = false;
        try
        {
            await callbackGate.WaitAsync(linked.Token);
            callbackGateAcquired = true;
            FeishuCallbackAcknowledgement acknowledgement = await (
                _apps ?? throw new AgwException(ErrorCodes.ExtensionUnavailable)
            ).ExecuteAsync(
                appId,
                async (db, app, ownerContext, token) =>
                {
                    if (app.Mode != FeishuAppMode.App || !app.Enabled)
                        return CallbackUnavailable();
                    return await FeishuCallbackProcessor.ProcessAsync(
                        db,
                        app,
                        ownerContext.ExtensionId,
                        ownerContext.JsonOptions,
                        notification.Payload,
                        token
                    );
                },
                linked.Token
            );
            return SerializeCallbackAcknowledgement(acknowledgement);
        }
        catch (OperationCanceledException)
            when (timeout.IsCancellationRequested || cancellationToken.IsCancellationRequested)
        {
            return SerializeCallbackAcknowledgement(CallbackUnavailable());
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            _logger.LogError(exception, "Feishu card action failed for application {AppId}", appId);
            return SerializeCallbackAcknowledgement(CallbackUnavailable());
        }
        finally
        {
            if (callbackGateAcquired)
                callbackGate.Release();
        }
    }

    private static FeishuCallbackAcknowledgement CallbackUnavailable() =>
        new()
        {
            State = FeishuCallbackState.Rejected,
            Message = "This action could not be processed.",
            Card = FeishuCards.CallbackCard("Action unavailable", "Please try again later."),
            Toast = FeishuCards.CallbackToast("error", "Please try again later."),
        };

    private static JsonElement SerializeCallbackAcknowledgement(FeishuCallbackAcknowledgement acknowledgement) =>
        JsonSerializer.SerializeToElement(acknowledgement, FeishuApiJsonContext.Default.FeishuCallbackAcknowledgement);

    private async ValueTask ProcessBindingMessageAsync(
        FeishuDbContext db,
        FeishuApp app,
        IExtensionContext context,
        FeishuEventMessage message,
        string? requestedCode,
        CancellationToken cancellationToken
    )
    {
        DateTimeOffset now = _timeProvider.GetUtcNow();
        FeishuBindRequest? pending = null;
        string code;
        if (requestedCode is not null)
        {
            pending = await db.BindRequests.SingleOrDefaultAsync(
                row => row.RootId == app.Id && row.CodeHash == HashCode(requestedCode) && row.ExpiresAt > now,
                cancellationToken
            );
            if (pending is null || pending.OpenId is not null && pending.OpenId != message.OpenId)
            {
                await _nodeBridge.SendTextAsync(
                    ToNodeApplication(context, app),
                    message.TargetKind,
                    message.ReceiveId,
                    "That Agw binding code is invalid or expired. Send /bind to start again.",
                    $"bind-invalid:{Guid.CreateVersion7():D}",
                    cancellationToken
                );
                return;
            }
            pending.OpenId = message.OpenId;
            code = requestedCode;
        }
        else
        {
            code = WebEncoders.Base64UrlEncode(RandomNumberGenerator.GetBytes(24));
            db.BindRequests.Add(
                new FeishuBindRequest
                {
                    RootId = app.Id,
                    OpenId = message.OpenId,
                    CodeHash = HashCode(code),
                    ExpiresAt = now.AddMinutes(10),
                }
            );
        }
        await db.SaveChangesAsync(cancellationToken);

        string baseUrl = context.HostCapabilities.GetRequired<IExtensionServerAddress>().GetPublicBaseUrl();
        string confirmUrl = new Uri(
            new Uri(baseUrl, UriKind.Absolute),
            $"api/ext/feishu/bind/confirm?code={Uri.EscapeDataString(code)}"
        ).ToString();
        await _nodeBridge.SendApplicationCardAsync(
            ToNodeApplication(context, app),
            message.TargetKind,
            message.ReceiveId,
            FeishuCards.BindCard(confirmUrl),
            $"bind:{app.Id:D}:{HashCode(code)}",
            cancellationToken
        );
    }

    private async Task WaitForScanOrIntervalAsync(CancellationToken cancellationToken)
    {
        using var waitCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        Task scan = _runtimeState.WaitForScanAsync(waitCancellation.Token).AsTask();
        Task interval = Task.Delay(ScanInterval, _timeProvider, cancellationToken);
        Task completed = await Task.WhenAny(scan, interval);
        waitCancellation.Cancel();
        if (completed == scan)
            await scan;
        else
            await interval;
    }

    private async Task StopConnectionsAsync()
    {
        ManagedConnection[] connections = _connections.Values.ToArray();
        foreach (ManagedConnection connection in connections)
            connection.Cancellation.Cancel();
        if (connections.Length > 0)
            await Task.WhenAll(connections.Select(connection => connection.Task));
    }

    private static bool TryReadMessage(JsonElement payload, out FeishuEventMessage message)
    {
        message = default!;
        JsonElement eventData = TryGetProperty(payload, "event", out JsonElement nestedEvent) ? nestedEvent : payload;
        if (
            !TryGetProperty(eventData, "message", out JsonElement messageData)
            || !TryGetString(messageData, "chat_id", out string chatId)
            || !TryGetString(messageData, "chat_type", out string chatType)
            || !TryGetString(messageData, "content", out string content)
            || !TryGetProperty(eventData, "sender", out JsonElement sender)
            || !TryGetProperty(sender, "sender_id", out JsonElement senderId)
            || !TryGetString(senderId, "open_id", out string openId)
        )
            return false;
        string text;
        try
        {
            using JsonDocument contentDocument = JsonDocument.Parse(content);
            if (!TryGetString(contentDocument.RootElement, "text", out text))
                return false;
        }
        catch (JsonException)
        {
            return false;
        }
        bool isDirect = string.Equals(chatType, "p2p", StringComparison.OrdinalIgnoreCase);
        message = new FeishuEventMessage
        {
            AppOpenId = string.Empty,
            OpenId = openId,
            ReceiveId = isDirect ? openId : chatId,
            TargetKind = isDirect ? FeishuTargetKind.User : FeishuTargetKind.Chat,
            Text = text,
        };
        return true;
    }

    private static bool TryGetProperty(JsonElement element, string name, out JsonElement value)
    {
        value = default;
        return element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out value);
    }

    private static bool TryGetString(JsonElement element, string name, out string value)
    {
        value = string.Empty;
        if (!TryGetProperty(element, name, out JsonElement property) || property.ValueKind != JsonValueKind.String)
            return false;
        value = property.GetString() ?? string.Empty;
        return !string.IsNullOrWhiteSpace(value);
    }

    private static FeishuNodeApplication ToNodeApplication(IExtensionContext context, FeishuApp app) =>
        new()
        {
            OpenDomain = app.OpenDomain,
            AppId = app.AppId ?? string.Empty,
            AppSecret = Unprotect(context, app.Id, app.AppSecret),
        };

    private static string Unprotect(IExtensionContext context, Guid appId, string? value) =>
        string.IsNullOrEmpty(value) ? string.Empty : context.Secrets.Unprotect("app", appId, value);

    private static string HashCode(string code) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(code)));

    private sealed class ManagedConnection
    {
        public ManagedConnection(long version, CancellationTokenSource cancellation)
        {
            Version = version;
            Cancellation = cancellation;
        }

        public long Version { get; }

        public CancellationTokenSource Cancellation { get; }

        public SemaphoreSlim CallbackGate { get; } = new(1, 1);

        public Task Task { get; set; } = Task.CompletedTask;
    }

    private sealed record AppConnectionSettings(Guid Id, long Version, FeishuNodeApplication Application);
}
