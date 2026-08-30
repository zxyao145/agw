using Microsoft.Extensions.AI;

namespace Agw.Tools.Contracts.Abstractions;

public interface IAgwTool
{
    string Category => "Default";

    bool AllowInPlanMode => false;

    string Name { get; }

    AITool ToAITool();
}
