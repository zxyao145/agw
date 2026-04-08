using System.Text.Json.Serialization;

namespace Agw.Providers.Domain.Entities;

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum ProviderAuthType
{
    ApiKey = 0,
    EnvVariable = 1
}
