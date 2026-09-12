using System.Text.Json.Serialization;

namespace Agw.Settings.Contracts;

public sealed class SettingDocument
{
    [JsonIgnore]
    public required string ValueJson { get; init; }
    public required long Version { get; init; }
}
