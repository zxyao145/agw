using System.Diagnostics;
using System.Reflection;
using System.Text.Json;
using Agw.Agents.Execution.Agents.ExternalAgents.Codex;
using OpenAI.CodexSdk.AppServer;
using OpenAI.CodexSdk.AppServer.Protocol;
using Project = Agw.Shared.Data.Entities.Projects.Project;

namespace Agw.Agents.Tests;

public sealed class CodexAppServerIntegrationTests
{
    [Fact]
    public async Task StartAsync_AgwWorkspaceOptions_StartsRealThread()
    {
        var settings = JsonSerializer.Serialize(
            new { codexOptions = new { codexPathOverride = CodexAppServerFixture.ExecutablePath() } }
        );
        var options = Assert.IsType<CodexAgentRuntimeOptions>(
            CodexAgentRuntimeOptionsMapper.Build(
                settings,
                new Project { Workspace = AppContext.BaseDirectory },
                null,
                false,
                null,
                null,
                AgwPermissionMode.FullAccess
            )
        );
        await using var client = await CodexAppServerClient.StartAsync(
            options.ClientOptions,
            TestContext.Current.CancellationToken
        );

        var started = await client.ThreadStartAsync(
            options.AgentOptions.ThreadStartParams with
            {
                Ephemeral = true,
            },
            TestContext.Current.CancellationToken
        );

        Assert.False(string.IsNullOrWhiteSpace(started.Thread.Id));
        Assert.Equal(Path.TrimEndingDirectorySeparator(AppContext.BaseDirectory), started.Thread.Cwd);
    }

    [Fact]
    public async Task StartAsync_RealCli_HandshakesAndStartsEphemeralThread()
    {
        await using var client = await CodexAppServerClient.StartAsync(
            new CodexAppServerClientOptions { ExecutablePath = CodexAppServerFixture.ExecutablePath() },
            TestContext.Current.CancellationToken
        );

        var started = await client.ThreadStartAsync(
            new ThreadStartParams { Ephemeral = true },
            TestContext.Current.CancellationToken
        );

        Assert.False(string.IsNullOrWhiteSpace(started.Thread.Id));
    }

    [Fact]
    public async Task DisposeAsync_RealCli_StopsOwnedProcess()
    {
        var client = await CodexAppServerClient.StartAsync(
            new CodexAppServerClientOptions { ExecutablePath = CodexAppServerFixture.ExecutablePath() },
            TestContext.Current.CancellationToken
        );
        var process = (Process)
            typeof(CodexAppServerClient)
                .GetField("_process", BindingFlags.Instance | BindingFlags.NonPublic)!
                .GetValue(client)!;
        var processId = process.Id;

        await client.DisposeAsync();

        var processes = Process.GetProcesses();
        var processIds = processes.Select(candidate => candidate.Id).ToList();
        foreach (var candidate in processes)
        {
            candidate.Dispose();
        }

        Assert.DoesNotContain(processId, processIds);
    }
}
