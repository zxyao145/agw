using System.Text.Json.Serialization;

namespace Agw.Agents.Contracts.Execution;

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum PermissionMode
{
    [JsonStringEnumMemberName("fullAccess")]
    FullAccess,

    [JsonStringEnumMemberName("alwaysAsk")]
    AlwaysAsk,

    [JsonStringEnumMemberName("allowSameArguments")]
    AllowSameArguments,
}
