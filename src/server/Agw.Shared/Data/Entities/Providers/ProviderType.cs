using System.Text.Json.Serialization;

namespace Agw.Shared.Data.Entities.Providers;

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum ProviderType
{
    OpenAIChatCompletions = 0,
    OpenAIResponses = 1,
    Anthropic = 2,
}
