using System.Text.Json;
using Agw.Agents.Definitions.Domain.ValueObjects;

namespace Agw.Agents.Definitions.Agents;

public static class A2AEndpointCredentialsJson
{
    public static Dictionary<string, string> Serialize(A2AEndpointCredentials credentials) =>
        credentials.HeadersByOrigin.ToDictionary(
            group => group.Key,
            group => JsonSerializer.Serialize(group.Value),
            StringComparer.Ordinal
        );

    public static A2AEndpointCredentials Deserialize(Dictionary<string, string> stored) =>
        new(
            stored.Select(group =>
                KeyValuePair.Create(group.Key, JsonSerializer.Deserialize<Dictionary<string, string>>(group.Value)!)
            )
        );
}
