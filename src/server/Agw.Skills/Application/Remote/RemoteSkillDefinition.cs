using System.Text.Json;

namespace Agw.Skills.Application.Remote;

public sealed record RemoteSkillDefinition(
    string Name,
    string Description,
    string Instructions,
    IReadOnlyList<string> Tags
);

public static class RemoteSkillDefinitionSerializer
{
    private static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web);

    public static string Serialize(RemoteSkillDefinition definition) => JsonSerializer.Serialize(definition, Options);

    public static RemoteSkillDefinition? Deserialize(string content)
    {
        try
        {
            var definition = JsonSerializer.Deserialize<RemoteSkillDefinition>(content, Options);
            if (
                definition == null
                || string.IsNullOrWhiteSpace(definition.Name)
                || string.IsNullOrWhiteSpace(definition.Description)
                || string.IsNullOrWhiteSpace(definition.Instructions)
                || definition.Tags == null
            )
            {
                return null;
            }

            return definition;
        }
        catch (JsonException)
        {
            return null;
        }
    }
}
