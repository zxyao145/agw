using Agw.Shared.Exceptions;
using Microsoft.Extensions.AI;

namespace Agw.Tools.HumanInteraction;

public sealed class HumanInteractionRequiredAIFunction : DelegatingAIFunction
{
    private readonly IHumanInteractionProtocol _protocol;

    public HumanInteractionRequiredAIFunction(AIFunction innerFunction, IHumanInteractionProtocol protocol)
        : base(innerFunction)
    {
        ArgumentNullException.ThrowIfNull(protocol);
        _protocol = protocol;
    }

    public override object? GetService(Type serviceType, object? serviceKey = null) =>
        serviceKey == null && serviceType == typeof(IHumanInteractionProtocol)
            ? _protocol
            : base.GetService(serviceType, serviceKey);

    protected override async ValueTask<object?> InvokeCoreAsync(
        AIFunctionArguments arguments,
        CancellationToken cancellationToken
    )
    {
        var accessor =
            arguments.Services?.GetService(typeof(IHumanInteractionContextAccessor))
            as IHumanInteractionContextAccessor;
        var channel = accessor?.Current;
        if (channel == null)
        {
            throw new AgwException(
                ErrorCodes.AgentExecutionFailed,
                $"Human interaction tool '{Name}' requires an active interactive channel."
            );
        }

        var currentCall = FunctionInvokingChatClient.CurrentContext?.CallContent;
        var request = _protocol.CreateRequest(arguments) with
        {
            Source = HumanInteractionToolMetadata.ReadSource(FunctionInvokingChatClient.CurrentContext?.Options) with
            {
                ToolName = currentCall?.Name ?? Name,
                CallId = currentCall?.CallId,
            },
        };
        var response = await channel.RequestAsync(request, cancellationToken).ConfigureAwait(false);

        if (response.Cancelled)
        {
            return _protocol.CreateCancelledResult(arguments, response);
        }

        var boundArguments = _protocol.BindResponse(arguments, response);
        return await base.InvokeCoreAsync(boundArguments, cancellationToken).ConfigureAwait(false);
    }
}
