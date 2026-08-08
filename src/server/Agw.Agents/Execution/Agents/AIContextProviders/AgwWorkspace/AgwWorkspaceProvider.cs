using Agw.Shared.Data.Entities.Agents;
using Agw.Shared.Data.Entities.Projects;

using Microsoft.Agents.AI;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Agw.Agents.Execution.Agents.AIContextProviders.AgwWorkspace;

internal sealed class AgwWorkspaceProvider : AIContextProvider
{
    private readonly Agent _agent;
    private readonly Project _project;
    private readonly IReadOnlyList<IAgentInstructionsSource> _sources;
    private readonly ILogger _logger;

    public AgwWorkspaceProvider(
        Agent agent,
        Project project,
        IEnumerable<IAgentInstructionsSource> sources,
        ILogger? logger = null)
    {
        _agent = agent;
        _project = project;
        _sources = sources.ToArray();
        _logger = logger ?? NullLogger.Instance;
    }

    /// <summary>
    /// 返回需要与输入上下文合并的 AIContext。默认合并规则：
    /// 最终 Instructions = 当前 Instructions + Provider Instructions
    /// 最终 Messages     = 当前 Messages + Provider Messages
    /// 最终 Tools        = 当前 Tools + Provider Tools
    /// 逻辑在 AIContextProvider.InvokingCoreAsync
    /// </summary>
    /// <param name="context"></param>
    /// <param name="cancellationToken"></param>
    /// <returns></returns>
    protected override async ValueTask<AIContext> ProvideAIContextAsync(
        InvokingContext context,
        CancellationToken cancellationToken = default)
    {
        var instructions = new List<string>(_sources.Count);
        var sourceContext = new AgwInstructionsSourceContext(_agent, _project, context);
        foreach (var source in _sources)
        {
            var value = await source
                .GetInstructionsAsync(sourceContext, cancellationToken)
                .ConfigureAwait(false);
            if (!string.IsNullOrWhiteSpace(value))
            {
                instructions.Add(value.Trim());
            }
        }

        _logger.LogDebug("Agw workspace provider started");

        return new AIContext
        {
            Instructions = instructions.Count == 0
                ? null
                : string.Join(Environment.NewLine, instructions),
        };
    }

}
