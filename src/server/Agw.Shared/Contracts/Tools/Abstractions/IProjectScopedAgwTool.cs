using Microsoft.Extensions.AI;

namespace Agw.Shared.Contracts.Tools.Abstractions;

public interface IProjectScopedAgwTool : IAgwTool
{
    AITool ToAITool(Guid projectId);
}
