using System.Text.Json.Serialization;

namespace Agw.Providers.Contracts;

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum ProviderType
{
    OpenAIChatCompletions = 0,
    OpenAIResponses = 1,
    Anthropic = 2,
}
