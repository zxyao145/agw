using System.Text.Json.Serialization;

namespace Agw.Shared.Data.Entities.Skills;

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum SkillKind
{
    BuiltIn = 0,
    Local = 1,
    Remote = 2,
}
