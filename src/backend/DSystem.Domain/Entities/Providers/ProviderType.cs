using System.Text.Json.Serialization;

namespace DSystem.Domain.Entities;

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum ProviderType
{
    OpenAI,
    Anthropic,
    GoogleGemini,
    GitHubCopilot
}
