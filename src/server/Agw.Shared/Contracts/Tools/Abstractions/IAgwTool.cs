using Microsoft.Extensions.AI;

namespace Agw.Shared.Contracts.Tools.Abstractions;

public interface IAgwTool
{
    string Category => "Default";

    bool AllowInPlanMode => false;

    string Name { get; }

    AITool ToAITool();
}
