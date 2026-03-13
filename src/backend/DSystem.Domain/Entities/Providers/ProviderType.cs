using System.Text.Json.Serialization;

namespace DSystem.Domain.Entities;

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum ProviderType
{
    OpenAI = 0,
    Anthropic = 1,
}
