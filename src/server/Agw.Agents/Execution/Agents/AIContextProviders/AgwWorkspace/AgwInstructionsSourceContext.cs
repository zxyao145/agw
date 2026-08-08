using Agw.Shared.Data.Entities.Agents;
using Agw.Shared.Data.Entities.Projects;

using Microsoft.Agents.AI;

namespace Agw.Agents.Execution.Agents.AIContextProviders.AgwWorkspace;

/// <summary>
/// Identifies the configured agent, project, and current invocation for an instructions source.
/// </summary>
public sealed record AgwInstructionsSourceContext
{
    public AgwInstructionsSourceContext(
        Agent agent,
        Project project,
        AIContextProvider.InvokingContext invocation)
    {
        Agent = agent;
        Project = project;
        Invocation = invocation;
    }

    public Agent Agent { get; }

    public Project Project { get; }

    public AIContextProvider.InvokingContext Invocation { get; }
}
