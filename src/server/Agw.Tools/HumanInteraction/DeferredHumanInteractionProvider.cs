using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;

namespace Agw.Tools.HumanInteraction;

/// <summary>Runs after tool-producing providers, including dynamically generated mode tools.</summary>
public sealed class DeferredHumanInteractionProvider : AIContextProvider
{
    public override IReadOnlyList<string> StateKeys => [];

    protected override ValueTask<AIContext> InvokingCoreAsync(
        InvokingContext context,
        CancellationToken cancellationToken = default
    )
    {
        if (context.AIContext.Tools is { } tools)
            context.AIContext.Tools = tools
                .Select(tool =>
                    tool is AIFunction function
                    && function.GetService<IHumanInteractionProtocol>() is not null
                    && function.GetService<ApprovalRequiredAIFunction>() is null
                        ? new ApprovalRequiredAIFunction(function)
                        : tool
                )
                .ToArray();
        return ValueTask.FromResult(context.AIContext);
    }
}
