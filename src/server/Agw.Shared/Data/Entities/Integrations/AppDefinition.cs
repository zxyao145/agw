using Agw.Shared.Contracts.Integrations;

namespace Agw.Shared.Data.Entities.Integrations;


/// <summary>
/// Static app catalog entry loaded from <see cref="IntegrationConstants.AppList"/>.
/// </summary>
public class AppDefinition
{
    /// <summary>
    /// Unique key
    /// </summary>
    public required string Name { get; init; }
    public required string DisplayName { get; init; }
    public required CategoryType Category { get; init; }
    public required string Provider { get; init; }
    public required string Description { get; init; }
    public required string AuthUrl { get; init; }
    public required string TokenEndpoint { get; init; }
    public string? SubjectField { get; init; }
    /// <summary>
    /// default oauth scopes
    /// </summary>
    public required List<string> Scopes { get; init; }

    /// <summary>
    /// default value for AppInstance.UsePkce 
    /// </summary>
    public bool UsePkce { get; set; } = true;


    public List<string> Tags { get; set; } = [];
    public required List<string> ToolNames { get; set; }
}

