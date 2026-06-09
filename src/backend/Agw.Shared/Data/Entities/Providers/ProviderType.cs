using System.Text.Json.Serialization;

namespace Agw.Shared.Data.Entities.Providers;

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum ProviderType
{
    OpenAI = 0,
    Anthropic = 1,
}
