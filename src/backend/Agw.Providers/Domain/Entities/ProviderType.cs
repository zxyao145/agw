using System.Text.Json.Serialization;

namespace Agw.Providers.Domain.Entities;

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum ProviderType
{
    OpenAI = 0,
    Anthropic = 1,
}
