using Agw.Shared.Utils;

namespace Agw.Agents.Execution.Agents.Context.Workspace;

internal sealed class ProjectInstructionsSource : IAgentInstructionsSource
{
    public ValueTask<string?> GetInstructionsAsync(
        AgwInstructionsSourceContext context,
        CancellationToken cancellationToken = default
    )
    {
        var configuredWorkspace = context.Project.Workspace;
        var workspace = PathUtil.ExpandTilde(
            string.IsNullOrEmpty(configuredWorkspace)
                ? ProjectDefaults.GetDefaultWorkspace(context.Project.Id)
                : configuredWorkspace
        );
        var instructions = $"""
            # others

            - Your default workspace or working directory is '{workspace}'.
            """;

        return ValueTask.FromResult<string?>(instructions);
    }
}
