using Microsoft.Agents.AI;

namespace Agw.Tools.ToolBlocks.Blocks.Mode;

public sealed class ModeToolBlock : IToolBlock
{
    internal const string PlanModeInstructions = """
        Use Plan mode only to understand the request and prepare a decision-complete plan.
        Explore existing project data with the available read-only tools, research as needed, and ask clarifying questions when requirements or tradeoffs are unresolved.
        Do not execute shell commands or mutate files, project memory, background tasks, Connections, MCP resources, or any other business data. Todo tools may be used to organize the proposed plan.
        When the plan is decision-complete and ready for approval, the final response must consist of exactly one block in this form:
        <proposed_plan>
        Markdown plan content
        </proposed_plan>
        Do not put these tags in a code fence. Do not use them for clarifying questions, interim updates, or ordinary answers, and do not write any preamble or closing text outside the block.
        After presenting the proposed plan, wait for explicit human approval before requesting a switch to Execute mode with mode_set.
        """;

    internal const string ExecuteModeInstructions = """
        Use Execute mode to carry out the user's request autonomously with the available tools.
        For a simple question, answer directly. For implementation work, inspect the current state, make the requested changes, and verify the outcome.
        """;

    private const string ModeInstructions = """
        ## Agent Mode
        Available modes:
        {available_modes}

        Current mode: {current_mode}
        Follow the instructions for the current mode. Use mode_get when the current mode must be checked. mode_set requires explicit human confirmation when requested by the Agent.
        """;

    public ToolBlockDescriptor Descriptor { get; } =
        new(
            ToolBlockNames.Mode,
            "Mode",
            "Allows the agent to switch between plan and execute modes.",
            ToolBlockScope.Agent | ToolBlockScope.Project,
            ["mode_set", "mode_get"]
        );

    public ValueTask<ToolContribution> MaterializeAsync(
        ToolBlockDefinition definition,
        ToolMaterializationContext context,
        CancellationToken cancellationToken
    )
    {
        var contribution = new ToolContribution();
        contribution.PlanModeAllowedToolNames.UnionWith(["mode_get", "mode_set"]);
        contribution.ContextProviders.Add(
            new AgentModeProvider(
                new AgentModeProviderOptions
                {
                    DefaultMode = context.DefaultMode,
                    Instructions = ModeInstructions,
                    Modes =
                    [
                        new AgentModeProviderOptions.AgentMode("plan", PlanModeInstructions),
                        new AgentModeProviderOptions.AgentMode("execute", ExecuteModeInstructions),
                    ],
                }
            )
        );
        contribution.ContextProviders.Add(new ModeSetHumanInteractionProvider());
        return ValueTask.FromResult(contribution);
    }
}
