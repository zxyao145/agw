using Microsoft.Extensions.AI;

namespace Agw.Tools.Contracts.Abstractions;

public interface IProjectScopedAgwTool : IAgwTool
{
    AITool ToAITool(Guid projectId);
}
