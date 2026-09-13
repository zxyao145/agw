using System.Text.Json.Serialization;

namespace Agw.Skills.Contracts;

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum SkillKind
{
    BuiltIn = 0,
    Local = 1,
    Remote = 2,
}
