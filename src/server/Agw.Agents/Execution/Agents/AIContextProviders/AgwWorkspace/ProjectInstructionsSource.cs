using Agw.Files.Utils;

namespace Agw.Agents.Execution.Agents.AIContextProviders.AgwWorkspace;

internal sealed class ProjectInstructionsSource : IAgentInstructionsSource
{
    public ValueTask<string?> GetInstructionsAsync(
        AgwInstructionsSourceContext context,
        CancellationToken cancellationToken = default
    )
    {
        var workspace = PathUtil.ExpandTilde(context.Project.GetMustWorkspace());
        var instructions = $"""
            # others

            - Your default workspace or working directory is '{workspace}'.
            """;

        return ValueTask.FromResult<string?>(instructions);
    }
}
