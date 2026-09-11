using System.Text.Json.Serialization;

namespace Agw.Tools.Abstractions;

[JsonConverter(typeof(JsonStringEnumConverter<AgwToolPermission>))]
public enum AgwToolPermission
{
    [JsonStringEnumMemberName("none")]
    None,

    [JsonStringEnumMemberName("readOnly")]
    ReadOnly,

    [JsonStringEnumMemberName("write")]
    Write,

    [JsonStringEnumMemberName("execute")]
    Execute,
}
