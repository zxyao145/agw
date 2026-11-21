using System.Text.Json.Nodes;

namespace PiAgentSdk;

internal static class PiCommands
{
    public static JsonObject GetState() => new() { ["type"] = "get_state" };

    public static JsonObject Abort() => new() { ["type"] = "abort" };

    public static JsonObject ClearQueue() => new() { ["type"] = "clear_queue" };

    public static JsonObject Prompt(string message, IReadOnlyList<PiImage>? images) =>
        WithMessage("prompt", message, images);

    public static JsonObject Steer(string message, IReadOnlyList<PiImage>? images) =>
        WithMessage("steer", message, images);

    private static JsonObject WithMessage(string type, string message, IReadOnlyList<PiImage>? images)
    {
        var command = new JsonObject { ["type"] = type, ["message"] = message };
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
