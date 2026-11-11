using System.Collections.Concurrent;
using System.Threading.Channels;
using Agw.Extensions.Sdk;
using Agw.Shared.Exceptions;

namespace Agw.Extensions.Feishu;

public sealed class FeishuRuntimeState
{
    private readonly ConcurrentDictionary<Guid, FeishuConnectionStatus> _statuses = new();
    private readonly ConcurrentDictionary<Guid, CancellationTokenSource> _connections = new();
    private readonly Channel<bool> _scanRequests = Channel.CreateBounded<bool>(
        new BoundedChannelOptions(1)
        {
            FullMode = BoundedChannelFullMode.DropOldest,
            SingleReader = true,
            SingleWriter = false,
        }
    );
    private IExtensionSecrets? _secrets;
    private Guid _extensionId;

    public void Initialize(IExtensionContext context)
    {
        _extensionId = context.ExtensionId;
        _secrets = context.Secrets;
    }

    public void Clear()
    {
        _secrets = null;
        _extensionId = Guid.Empty;
    }

    public FeishuNodeApplication ToNodeApplication(FeishuApp app) =>
        new()
        {
            OpenDomain = app.OpenDomain,
            AppId = app.AppId ?? string.Empty,
            AppSecret = Unprotect(app.Id, app.AppSecret),
        };

    public string Unprotect(Guid appId, string? value) =>
        string.IsNullOrEmpty(value)
            ? string.Empty
            : (_secrets ?? throw new AgwException(ErrorCodes.ExtensionUnavailable)).Unprotect("app", appId, value);

    public Guid ExtensionId => _extensionId;

    public FeishuConnectionStatus GetStatus(FeishuApp app) =>
        _statuses.TryGetValue(app.Id, out FeishuConnectionStatus? status)
            ? status
            : new FeishuConnectionStatus(
                app.Mode == FeishuAppMode.App && app.Enabled ? "Connecting" : "Disconnected",
                null
            );

    public void SetStatus(Guid appId, string state, string? error = null) =>
        _statuses[appId] = new FeishuConnectionStatus(state, error);

    public void RegisterConnection(Guid appId, CancellationTokenSource cancellation)
    {
        if (!_connections.TryAdd(appId, cancellation))
            throw new AgwException(ErrorCodes.ExtensionOperationConflict);
    }

    public void RemoveConnection(Guid appId, CancellationTokenSource cancellation, bool retainFailure = false)
    {
        _connections.TryRemove(new KeyValuePair<Guid, CancellationTokenSource>(appId, cancellation));
        if (
            !retainFailure
            || !_statuses.TryGetValue(appId, out FeishuConnectionStatus? status)
            || status.State != "Failed"
        )
            _statuses.TryRemove(appId, out _);
    }

    public void RequestScan(Guid? appId = null)
    {
        if (appId is { } id && _connections.TryGetValue(id, out CancellationTokenSource? connection))
            connection.Cancel();
        _scanRequests.Writer.TryWrite(true);
    }

    public async ValueTask WaitForScanAsync(CancellationToken cancellationToken)
    {
        await _scanRequests.Reader.ReadAsync(cancellationToken);
    }
}

public sealed record FeishuConnectionStatus(string State, string? Error);
