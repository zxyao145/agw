using System.Text.Json.Serialization;

namespace Agw.Agents.Execution.Commands.Setting;

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
