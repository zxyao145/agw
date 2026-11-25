using System.Text.Json;
using Agw.Shared.Data.Entities.Agents;
using Agw.Shared.Data.Entities.Projects;
using Agw.Shared.Utils;
using Microsoft.EntityFrameworkCore;

namespace Agw.Agents.Tests.Acp;

internal sealed class AcpAgentHarness(ExternalAgentHarness runtime) : IAsyncDisposable
{
    internal ExternalAgentHarness Runtime => runtime;
    internal static CancellationToken Ct => TestContext.Current.CancellationToken;
    private AcpTestAgent? _fixture;
    public string LogPath { get; private set; } = "";
    public IReadOnlyList<JsonElement> Log => _fixture!.ReadLog(LogPath);

    public static IDisposable EnterUser() => ExternalAgentHarness.EnterUser();

    public static async Task<AcpAgentHarness> CreateAsync(
        string extra,
        IReadOnlyDictionary<string, string>? env = null,
        string? postgres = null,
        Action<Dictionary<string, string?>>? configure = null
    )
    {
        var runtime = await ExternalAgentHarness.CreateAsync(
            new Agent
            {
                Type = AgentType.External,
                ExternalAgentKind = EngineKind.ACP,
                Name = "acp",
                DisplayName = "ACP",
                Extra = extra,
                EnvironmentVariables = env?.ToDictionary() ?? [],
            },
            new(),
            postgres: postgres,
            configure: configure
        );
        return new(runtime);
    }

    public static async Task<AcpAgentHarness> ScenarioAsync(
        object scenario,
        object? settings = null,
        IReadOnlyDictionary<string, string>? env = null,
        string? postgres = null,
        Action<Dictionary<string, string?>>? configure = null
    )
    {
        var fixture = await AcpTestAgent.EnsureInstalledAsync();
        var (path, log) = fixture.CreateScenario(scenario);
        var extra = JsonSerializer.SerializeToNode(settings ?? new { })!.AsObject();
        extra["command"] = "node";
        extra["args"] = JsonSerializer.SerializeToNode(new[] { fixture.ScriptPath, path, log });
        var harness = await CreateAsync(extra.ToJsonString(), env, postgres, configure);
        harness._fixture = fixture;
        harness.LogPath = log;
        return harness;
    }

    public static object Chunk(string text, string kind = "agent_message_chunk") =>
        new { sessionUpdate = kind, content = new { type = "text", text } };

    public static object Text(string text) => new { update = Chunk(text) };

    public static object Turn(params object[] steps) => new { steps };

    public IReadOnlyList<JsonElement> Requests(string method) =>
        Log.Where(row => row.TryGetProperty("method", out var value) && value.GetString() == method)
            .Select(row => row.GetProperty("params"))
            .ToArray();

    public IReadOnlyList<JsonElement> Events(string name) =>
        Log.Where(row => row.TryGetProperty("event", out var value) && value.GetString() == name).ToArray();

    public Task<Guid> StartTurnAsync(string text = "go", AgwPermissionMode? permission = null) =>
        runtime.StartTurnAsync(text, permission);

    public async Task<Guid> RunTurnAsync(string text = "go", AgwPermissionMode? permission = null)
    {
        var id = await StartTurnAsync(text, permission);
        await WaitIdleAsync();
        return id;
    }

    public Task WaitIdleAsync() => runtime.WaitIdleAsync();

    public void Interrupt() => runtime.Interrupt();

    public Task RebuildCoordinatorAsync() => runtime.RebuildCoordinatorAsync();

    public Task ClearConversationAsync() => runtime.ClearConversationAsync();

    public IReadOnlyList<AgwMessage> ReadMessages(Guid id) => runtime.ReadMessages(id);

    public Task<ProjectConversationTurn> ReadTurnAsync(Guid id) => runtime.ReadTurnAsync(id);

    public Task<List<ProjectConversationChatHistory>> ReadHistoryAsync() => runtime.ReadHistoryAsync();

    public Task<string?> ReadProviderSessionIdAsync() =>
        runtime.QueryAsync(db =>
            db.ProjectConversationBindings.Where(row =>
                    row.ProjectConversationId == runtime.ConversationId
                    && row.AgentId == runtime.AgentId
                    && row.IsActive
                )
                .Select(row => row.ProviderSessionId)
                .SingleOrDefaultAsync(Ct)
        );

    public async Task<ToolApprovalInteraction> WaitForInteractionAsync(Guid turnId, int index = 0)
    {
        await WaitUntilAsync(() => ReadMessages(turnId).Count(AgwMessageClassifier.IsInteractionRequest) > index);
        var message = ReadMessages(turnId).Where(AgwMessageClassifier.IsInteractionRequest).ElementAt(index);
        var payload = Assert.IsType<JsonElement>(message.AdditionalProperties!["interaction"]);
        return Assert.IsType<ToolApprovalInteraction>(payload.Deserialize<InteractionRequest>(WebJsonOptions.Default));
    }

    public Task SubmitApprovalAsync(string interactionId, bool approved) =>
        SubmitAsync(new ToolApprovalDecision { InteractionId = interactionId, Approved = approved });

    private async Task SubmitAsync(ToolApprovalDecision decision) =>
        Assert.True(await runtime.TurnHost!.TrySubmitHumanResponseAsync(decision, Ct));

    public static async Task WaitUntilAsync(Func<bool> condition)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(Ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(15));
        while (!condition())
            await Task.Delay(20, timeout.Token);
    }

    public async Task AssertFailureAsync(Guid id, int code, string? message = null)
    {
        var turn = await ReadTurnAsync(id);
        Assert.Equal(ProjectConversationTurnStatus.Failed, turn.Status);
        Assert.Equal(code.ToString(), turn.ErrorCode);
        var messages = ReadMessages(id);
        Assert.Equal(code.ToString(), messages[^1].AdditionalProperties?["errorCode"]?.ToString());
        if (message is not null)
            Assert.Contains(
                messages.SelectMany(item => item.Contents).OfType<AgwErrorContent>(),
                error => error.Content.Contains(message, StringComparison.Ordinal)
            );
    }

    public async Task AssertCompletedAsync(Guid turnId)
    {
        var turn = await ReadTurnAsync(turnId);
        Assert.True(
            turn.Status == ProjectConversationTurnStatus.Completed,
            string.Join(
                "\n",
                ReadMessages(turnId)
                    .SelectMany(message => message.Contents)
                    .OfType<AgwErrorContent>()
                    .Select(error => error.Content)
            )
        );
    }

    public ValueTask DisposeAsync() => runtime.DisposeAsync();
}
