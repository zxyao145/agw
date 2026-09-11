using Microsoft.Extensions.AI;

namespace Agw.Tools.Abstractions;

public interface IProjectScopedAgwTool : IAgwToolMeta
{
    AITool ToAITool(Guid projectId);
}
