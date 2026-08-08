using Agw.Shared.Contracts.Agents;

using Microsoft.Extensions.AI;

namespace Agw.Tools.HumanInteraction;

public interface IHumanInteractionProtocol
{
    HumanInteractionRequest CreateRequest(
        string requestId,
        AIFunctionArguments arguments);

    AIFunctionArguments BindResponse(
        AIFunctionArguments arguments,
        HumanInteractionResponse response);

    object? CreateCancelledResult(
        AIFunctionArguments arguments,
        HumanInteractionResponse response);
}
