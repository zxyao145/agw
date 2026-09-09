using Microsoft.Extensions.AI;

namespace Agw.Tools.HumanInteraction;

public interface IHumanInteractionProtocol
{
    UserInputRequest CreateRequest(AIFunctionArguments arguments);

    AIFunctionArguments BindResponse(AIFunctionArguments arguments, UserInputResponse response);

    object? CreateCancelledResult(AIFunctionArguments arguments, UserInputResponse response);
}
