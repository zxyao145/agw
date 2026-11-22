using Agw.Agents.Definitions.Domain.Rules;

namespace Agw.Agents.Definitions.Agents;

public sealed class AgentCreateCommand
{
    public IEnumerable<Guid>? McpToolServerIds { get; init; }
    public IEnumerable<Guid>? SkillIds { get; init; }
    public IEnumerable<Guid>? ConnectionIds { get; init; }
    public A2AEndpointInput? A2AEndpoint { get; init; }
}
