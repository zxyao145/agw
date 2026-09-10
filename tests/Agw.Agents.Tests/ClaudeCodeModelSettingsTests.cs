using System.Text.Json.Nodes;
using Agw.Agents.Execution.Agents.ExternalAgents.ClaudeCode;
using ClaudeCodeSdk.MAF;

namespace Agw.Agents.Tests;

public class ClaudeCodeModelSettingsTests
{
    [Fact]
    public void Create_ConflictingSettingsEnv_ModelSelectionWinsWithoutModifyingSource()
    {
        var original = Options() with
        {
            Settings =
                """{"env":{"ANTHROPIC_BASE_URL":"https://wrong.invalid","ANTHROPIC_API_KEY":"wrong-key","ANTHROPIC_AUTH_TOKEN":"wrong-token","KEEP":"value"},"hooks":{},"alwaysThinkingEnabled":true}""",
        };
        using var settings = ClaudeCodeModelSettings.Create(original);
        var document = JsonNode.Parse(File.ReadAllText(settings.Options.Settings!))!;

        Assert.Equal("https://selected.invalid/anthropic", document["env"]!["ANTHROPIC_BASE_URL"]!.GetValue<string>());
        Assert.Equal("selected-key", document["env"]!["ANTHROPIC_API_KEY"]!.GetValue<string>());
        Assert.Equal("", document["env"]!["ANTHROPIC_AUTH_TOKEN"]!.GetValue<string>());
        Assert.Equal("value", document["env"]!["KEEP"]!.GetValue<string>());
        Assert.True(document["alwaysThinkingEnabled"]!.GetValue<bool>());
        Assert.NotNull(document["hooks"]);
        Assert.DoesNotContain("selected-key", settings.Options.Settings!);
        Assert.Contains("wrong-key", original.Settings!);
        if (!OperatingSystem.IsWindows())
        {
            Assert.Equal(
                UnixFileMode.UserRead | UnixFileMode.UserWrite,
                File.GetUnixFileMode(settings.Options.Settings!)
            );
        }
    }

    [Fact]
    public async Task Create_ExplicitSettingsFile_OverridesCopyAndDeletesOnlyTemporaryFile()
    {
        var directory = Directory.CreateTempSubdirectory("agw-settings-test-");
        var source = Path.Combine(directory.FullName, "source.json");
        const string content = """{"env":{"ANTHROPIC_AUTH_TOKEN":"wrong-token"},"verbose":true}""";
        await File.WriteAllTextAsync(source, content, TestContext.Current.CancellationToken);
        try
        {
            var original = Options() with
            {
                WorkingDirectory = directory.FullName,
                ExtraArgs = new Dictionary<string, string?> { ["settings"] = "source.json", ["verbose"] = null },
            };
            var settings = ClaudeCodeModelSettings.Create(original);
            var runtimePath = settings.Options.Settings!;
            Assert.DoesNotContain("settings", settings.Options.ExtraArgs.Keys);
            Assert.Contains("verbose", settings.Options.ExtraArgs.Keys);
            Assert.True(
                JsonNode.Parse(await File.ReadAllTextAsync(runtimePath, TestContext.Current.CancellationToken))![
                    "verbose"
                ]!.GetValue<bool>()
            );
            await settings.DisposeAsync();
            await settings.DisposeAsync();

            Assert.False(File.Exists(runtimePath));
            Assert.False(Directory.Exists(Path.GetDirectoryName(runtimePath)));
            Assert.Equal(content, await File.ReadAllTextAsync(source, TestContext.Current.CancellationToken));
        }
        finally
        {
            directory.Delete(recursive: true);
        }
    }

    [Fact]
    public void Create_TwoExecutions_UseIndependentFiles()
    {
        using var first = ClaudeCodeModelSettings.Create(Options());
        using var second = ClaudeCodeModelSettings.Create(
            Options() with
            {
                EnvironmentVariables = new Dictionary<string, string?> { ["ANTHROPIC_API_KEY"] = "other-key" },
            }
        );
        Assert.NotEqual(first.Options.Settings, second.Options.Settings);
        Assert.Contains("selected-key", File.ReadAllText(first.Options.Settings!));
        Assert.DoesNotContain("other-key", File.ReadAllText(first.Options.Settings!));
        Assert.Contains("other-key", File.ReadAllText(second.Options.Settings!));
    }

    private static ClaudeCodeAIAgentOptions Options() =>
        new()
        {
            EnvironmentVariables = new Dictionary<string, string?>
            {
                ["ANTHROPIC_BASE_URL"] = "https://selected.invalid/anthropic",
                ["ANTHROPIC_API_KEY"] = "selected-key",
                ["ANTHROPIC_AUTH_TOKEN"] = null,
            },
        };
}
