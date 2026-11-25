using Agw.Agents.Execution.Agents.ExternalAgents.Acp.Protocol;
using StreamJsonRpc;

namespace Agw.Agents.Execution.Agents.ExternalAgents.Acp;

internal interface IAcpClientHandler
{
    void OnSessionUpdate(AcpSessionNotification notification);
    Task<AcpRequestPermissionResponse> OnRequestPermissionAsync(
        AcpRequestPermissionRequest request,
        CancellationToken ct
    );
}

internal sealed class AcpClientMethods(IAcpClientHandler handler)
{
    [JsonRpcMethod("session/update")]
    public void Update(AcpSessionNotification notification) => handler.OnSessionUpdate(notification);

    [JsonRpcMethod("session/request_permission")]
    public Task<AcpRequestPermissionResponse> RequestPermissionAsync(
        AcpRequestPermissionRequest request,
        CancellationToken ct
    ) => handler.OnRequestPermissionAsync(request, ct);
}
