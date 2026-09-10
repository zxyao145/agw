namespace Agw.Tools.Contracts.Abstractions;

public interface IAgwToolMeta
{
    string Category => "Default";

    bool AllowInPlanMode => false;

    AgwToolPermission RequiredPermission { get; }

    string Name { get; }
}
