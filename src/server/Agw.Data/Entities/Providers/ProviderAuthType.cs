using System.Text.Json.Serialization;

namespace Agw.Shared.Data.Entities.Providers;

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum ProviderAuthType
{
    ApiKey = 0,
    EnvVariable = 1
}
