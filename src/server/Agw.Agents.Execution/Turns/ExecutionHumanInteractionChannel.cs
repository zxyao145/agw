using Agw.Agents.Execution.Agentflows;
using Agw.Agents.Execution.Messaging;
using Microsoft.Extensions.AI;

namespace Agw.Agents.Execution.Turns;

internal sealed class ExecutionHumanInteractionChannel : IHumanInteractionChannel
{
    private readonly IHumanGateApprovalHandler _responseHandler;
    private readonly IExecutionMessageSink _messageSink;
    private readonly SemaphoreSlim _requestLock = new(1, 1);

    public ExecutionHumanInteractionChannel(
        IHumanGateApprovalHandler responseHandler,
        IExecutionMessageSink messageSink
    )
    {
        _responseHandler = responseHandler;
        _messageSink = messageSink;
    }

    public async ValueTask<HumanInteractionResponse> RequestAsync(
        HumanInteractionRequest request,
        CancellationToken cancellationToken
    )
    {
        ArgumentNullException.ThrowIfNull(request);

        await _requestLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return await RequestCoreAsync(request, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _requestLock.Release();
        }
    }

    private async ValueTask<HumanInteractionResponse> RequestCoreAsync(
        HumanInteractionRequest request,
        CancellationToken cancellationToken
    )
    {
        using var requestCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);

        var gateRequest = new HumanGateApprovalRequest(
            request.RequestId,
            "human-interaction",
            null,
            "interaction",
            request.Prompt,
            []
        );
        var pendingResponse = _responseHandler.WaitForApprovalAsync(gateRequest, requestCts.Token).AsTask();
        try
        {
            await _messageSink.WriteAsync(CreateMessage(request), cancellationToken);
            var decision = await pendingResponse.ConfigureAwait(false);
            return new HumanInteractionResponse(
                request.RequestId,
                Cancelled: !decision.Approved,
                decision.ResponseData
            );
        }
        catch
        {
            await requestCts.CancelAsync();
            try
            {
                await pendingResponse.ConfigureAwait(false);
            }
            catch (Exception)
            {
                // Preserve the original exception if the pending response faults while cleaning up.
            }
            throw;
        }
    }

    internal static AgwMessage CreateMessage(HumanInteractionRequest request)
    {
        var properties = new AdditionalPropertiesDictionary
        {
            { "type", "human-interaction-request" },
            { "requestId", request.RequestId },
            { "interactionKind", request.InteractionKind },
            { "prompt", request.Prompt },
            { "payload", request.Payload },
        };
        if (!string.IsNullOrWhiteSpace(request.ToolName))
        {
            properties["toolName"] = request.ToolName;
        }
        if (!string.IsNullOrWhiteSpace(request.CallId))
        {
            properties["callId"] = request.CallId;
        }

        return new AgwMessage(
            Guid.CreateVersion7().ToString("N"),
            Constants.DefaultAgentAuthor,
            AiRole.System,
            [new AgwTextContent { Content = request.Prompt }],
            properties
        );
    }
}
