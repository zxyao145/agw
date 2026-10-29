using System.Text.Json;

namespace Agw.Agents.Tests;

internal sealed class CodexLiveTestOptions
{
    private static readonly JsonSerializerOptions SerializerOptions = new(JsonSerializerDefaults.Web);

    public static string FilePath => Path.Combine(AppContext.BaseDirectory, "test-data", "codex-live.json");

    public static CodexLiveTestOptions? Current =>
        File.Exists(FilePath)
            ? JsonSerializer.Deserialize<CodexLiveTestOptions>(File.ReadAllText(FilePath), SerializerOptions)
                ?? throw new JsonException($"Codex test options in '{FilePath}' were empty.")
            : null;

    public bool RunProviderSessionTests { get; init; }

    public bool RunImageFlow { get; init; }

    public string? CodexPath { get; init; }

    public string? Model { get; init; }
}
