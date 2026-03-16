using System.Text.Json.Serialization;

namespace DSystem.Domain.Entities;

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum ProviderAuthType
{
    ApiKey = 0,
    EnvVariable = 1
}
