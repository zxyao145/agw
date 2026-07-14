using Agw.Shared.Data.Entities.Integrations;

namespace Agw.Integrations.Contracts.Manager;

public sealed record AppDefinitionListItemResponse(
    string Name,
    string DisplayName,
    CategoryType Category,
    string Provider,
    string Description,
    string AuthUrl,
    IReadOnlyList<string> Scopes,
    bool UsePkce,
    IReadOnlyList<string> Tags,
    IReadOnlyList<string> ToolNames);
