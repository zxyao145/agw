using System.Text.Json.Nodes;

namespace PiAgentSdk;

internal static class PiCommands
{
    public static JsonObject GetState() => new() { ["type"] = "get_state" };

    public static JsonObject Abort() => new() { ["type"] = "abort" };

    public static JsonObject Prompt(string message, IReadOnlyList<PiImage>? images)
    {
        var command = new JsonObject { ["type"] = "prompt", ["message"] = message };
        if (images is not { Count: > 0 })
        {
            return command;
        }

        var array = new JsonArray();
        foreach (var image in images)
        {
            array.Add(
                new JsonObject
                {
                    ["type"] = "image",
                    ["data"] = image.Data,
                    ["mimeType"] = image.MimeType,
                }
            );
        }

        command["images"] = array;
        return command;
    }
}
