using System.Text.Json;
using System.Text.Json.Nodes;
using Agw.Shared.Exceptions;
using ClaudeCodeSdk.MAF;

namespace Agw.Agents.Execution.Agents.ExternalAgents.ClaudeCode;

// Claude's settings.env overrides its inherited process environment. Use the higher-priority
// --settings file without putting credentials in command-line arguments or changing host files.
internal sealed class ClaudeCodeModelSettings : IDisposable, IAsyncDisposable
{
    private static readonly string[] ModelEnvironmentKeys =
    [
        "ANTHROPIC_BASE_URL",
        "ANTHROPIC_API_KEY",
        "ANTHROPIC_AUTH_TOKEN",
        "ANTHROPIC_MODEL",
        "CLAUDE_CODE_OAUTH_TOKEN",
        "CLAUDE_CODE_USE_BEDROCK",
        "CLAUDE_CODE_USE_VERTEX",
        "CLAUDE_CODE_USE_FOUNDRY",
    ];

    private readonly string _directory;
    private int _disposed;

    private ClaudeCodeModelSettings(string directory, ClaudeCodeAIAgentOptions options)
    {
        _directory = directory;
        Options = options;
    }

    public ClaudeCodeAIAgentOptions Options { get; }

    public static ClaudeCodeModelSettings Create(ClaudeCodeAIAgentOptions options)
    {
        var extraArgs = new Dictionary<string, string?>(options.ExtraArgs ?? new Dictionary<string, string?>());
        var settingsSource = extraArgs.Remove("settings", out var extraSettings) ? extraSettings : options.Settings;
        JsonObject settings;
        try
        {
            var json =
                string.IsNullOrWhiteSpace(settingsSource) ? "{}"
                : settingsSource.TrimStart().StartsWith('{') ? settingsSource
                : File.ReadAllText(
                    Path.GetFullPath(settingsSource, options.WorkingDirectory ?? Environment.CurrentDirectory)
                );
            settings =
                JsonNode.Parse(json) as JsonObject
                ?? throw new AgwException(ErrorCodes.InvalidParam, "Claude Code settings must be a JSON object.");
        }
        catch (JsonException)
        {
            throw new AgwException(ErrorCodes.InvalidParam, "Claude Code settings must be valid JSON.");
        }

        if (settings["env"] is not null and not JsonObject)
        {
            throw new AgwException(ErrorCodes.InvalidParam, "Claude Code settings.env must be a JSON object.");
        }
        var environment = settings["env"] as JsonObject ?? new JsonObject();
        foreach (var key in ModelEnvironmentKeys)
        {
            environment[key] = options.EnvironmentVariables?.GetValueOrDefault(key) ?? string.Empty;
        }
        if (settings["env"] == null)
        {
            settings["env"] = environment;
        }

        var directory = Directory.CreateTempSubdirectory("agw-claude-model-").FullName;
        try
        {
            if (!OperatingSystem.IsWindows())
            {
                File.SetUnixFileMode(
                    directory,
                    UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute
                );
            }
            var file = Path.Combine(directory, "settings.json");
            var fileOptions = new FileStreamOptions { Mode = FileMode.CreateNew, Access = FileAccess.Write };
            if (!OperatingSystem.IsWindows())
            {
                fileOptions.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
            }
            using (var stream = new FileStream(file, fileOptions))
            {
                JsonSerializer.Serialize(stream, settings);
            }
            return new ClaudeCodeModelSettings(directory, options with { Settings = file, ExtraArgs = extraArgs });
        }
        catch
        {
            Directory.Delete(directory, recursive: true);
            throw;
        }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 0)
        {
            Directory.Delete(_directory, recursive: true);
        }
    }

    public ValueTask DisposeAsync()
    {
        Dispose();
        return ValueTask.CompletedTask;
    }
}
