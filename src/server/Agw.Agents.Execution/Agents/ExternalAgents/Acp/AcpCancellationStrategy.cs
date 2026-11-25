using System.Collections.Concurrent;
using System.Text.Json;
using Microsoft.VisualStudio.Threading;
using StreamJsonRpc;

namespace Agw.Agents.Execution.Agents.ExternalAgents.Acp;

internal sealed class AcpCancellationStrategy : ICancellationStrategy
{
    private const string Method = "$/cancel_request";
    private readonly JsonRpc _rpc;
    private readonly ConcurrentDictionary<RequestId, CancellationTokenSource> _incoming = new();

    public AcpCancellationStrategy(JsonRpc rpc)
    {
        _rpc = rpc;
        rpc.AddLocalRpcMethod(Method, (Action<JsonElement>)CancelInbound);
    }

    public void CancelOutboundRequest(RequestId requestId) =>
        _rpc.NotifyWithParameterObjectAsync(Method, new { requestId = (object?)requestId.String ?? requestId.Number })
            .Forget();

    public void OutboundRequestEnded(RequestId requestId) { }

    public void IncomingRequestStarted(RequestId requestId, CancellationTokenSource cancellationTokenSource) =>
        _incoming[requestId] = cancellationTokenSource;

    public void IncomingRequestEnded(RequestId requestId) => _incoming.TryRemove(requestId, out _);

    private void CancelInbound(JsonElement requestId)
    {
        var id =
            requestId.ValueKind == JsonValueKind.String
                ? new RequestId(requestId.GetString()!)
                : new RequestId(requestId.GetInt64());
        if (_incoming.TryGetValue(id, out var source))
            source.CancelAsync().Forget();
    }
}
