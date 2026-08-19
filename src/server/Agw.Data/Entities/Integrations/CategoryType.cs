using System.Text.Json.Serialization;

namespace Agw.Shared.Data.Entities.Integrations;

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum CategoryType
{
    GitServer,

    //CloudStorage,
    //Chat,
    Other,
}
