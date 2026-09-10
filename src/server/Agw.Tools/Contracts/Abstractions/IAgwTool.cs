using Microsoft.Extensions.AI;

namespace Agw.Tools.Contracts.Abstractions;

public interface IAgwTool : IAgwToolMeta
{
    AITool ToAITool();
}
