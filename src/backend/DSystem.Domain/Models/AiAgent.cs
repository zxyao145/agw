namespace DSystem.Domain.Models;

/// <summary>
/// DTO representing all values required to construct an agent with Microsoft Agent Framework (e.g., an AIAgent instance).
/// </summary>
public class AiAgent
{
    public Guid Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public string SystemPrompt { get; set; } = string.Empty;
    public string ProviderName { get; set; } = string.Empty;
    public string ModelName { get; set; } = string.Empty;
    public string Endpoint { get; set; } = string.Empty;
    public string ApiKey { get; set; } = string.Empty;
}
