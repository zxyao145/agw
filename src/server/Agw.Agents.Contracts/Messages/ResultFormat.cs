using System.Text.Json.Serialization;

namespace Agw.Agents.Contracts.Messages;

/// <summary>The presentation format of an Agent's final Result.</summary>
[JsonConverter(typeof(JsonStringEnumConverter<ResultFormat>))]
public enum ResultFormat
{
    [JsonStringEnumMemberName("markdown")]
    Markdown,

    [JsonStringEnumMemberName("json")]
    Json,
}
