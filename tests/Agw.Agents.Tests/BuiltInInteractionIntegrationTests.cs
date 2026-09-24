using System.Runtime.CompilerServices;
using System.Text.Json;
using Agw.Agents.Execution.Agents.Composition;
using Agw.Agents.Execution.Agents.Tools;
using Agw.Agents.Execution.Context;
using Agw.Agents.Execution.HumanInteraction;
using Agw.Agents.Execution.HumanInteraction.Application;
using Agw.Agents.Execution.HumanInteraction.Infrastructure.Maf;
using Agw.Agents.Execution.HumanInteraction.InProcess;
using Agw.Shared.Data.Entities.Agents;
using Agw.Shared.Data.Entities.Projects;
using Agw.Shared.Tooling;
using Agw.Tools.Abstractions.ToolBlocks;
using Agw.Tools.HumanInteraction;
using Agw.Tools.Impl.ToolBlocks.Mode;
using Agw.Tools.Impl.Tools.Basic;
using Agw.Tools.Runtime;
using Agw.Tools.ToolBlocks;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace Agw.Agents.Tests;

public sealed class BuiltInInteractionIntegrationTests
{
    [Theory]
    [InlineData("ask_user_question", false, false)]
    [InlineData("ask_user_question", false, true)]
    [InlineData("ask_user_question", true, false)]
    [InlineData("ask_user_question", true, true)]
    [InlineData("mode_set", false, false)]
    [InlineData("mode_set", false, true)]
    [InlineData("mode_set", true, false)]
    [InlineData("mode_set", true, true)]
    public async Task BuiltInInput_BothLifecycles_BindsHumanResponseOrCancels(
        string toolName,
        bool durable,
        bool cancelled
    )
    {
        var token = TestContext.Current.CancellationToken;
        var accessor = new HumanInteractionContextAccessor(new AgentExecutionContextAccessor());
        await using var services = new ServiceCollection()
            .AddSingleton(accessor)
            .AddSingleton<IHumanInteractionContextAccessor>(accessor)
            .BuildServiceProvider();
        await using var modeTools = await new ToolBlockRegistry([new ModeToolBlock()]).MaterializeAsync(
            [new ModeToolBlockDefinition()],
            ToolBlockScope.Agent,
            new ToolMaterializationContext
            {
                Agent = new Agent(),
                Project = new Project(),
                Workspace = "/workspace",
                DefaultMode = "plan",
            },
            token
        );
        var providers = toolName == "mode_set" ? modeTools.ContextProviders.ToList() : new List<AIContextProvider>();
        if (durable)
            providers.Add(new DeferredHumanInteractionProvider());
        await using var capabilities = new AgentCapabilityComposition(
            toolName == "ask_user_question" ? [new AskUserQuestionTool().ToAITool()] : [],
            [],
            [],
            providers,
            [],
            [ToolApprovalAgent.AllToolsAutoApprovalRule],
            modeTools.PlanModeAllowedToolNames,
            [],
            new Dictionary<string, string>(),
            new AgentResourceLease()
        );
        var arguments =
            toolName == "mode_set"
                ? new Dictionary<string, object?> { ["mode"] = "execute" }
                : new Dictionary<string, object?>
                {
                    ["questions"] = new[]
                    {
                        new AskUserQuestionQuestion
                        {
                            Question = "Database?",
                            Header = "Database",
                            Options =
                            [
                                new() { Label = "PostgreSQL", Description = "Server" },
                                new() { Label = "SQLite", Description = "Local" },
                            ],
                        },
                    },
                    ["answers"] = new Dictionary<string, string> { ["Database?"] = "FORGED" },
                };
        using var model = new BuiltInModel(new FunctionCallContent("builtin-call", toolName, arguments));
        var agent = model.AsAgwAgent(
            new ResolvedAgentDefinition
            {
                Id = "built-in",
                Name = "Built-in",
                ModelId = "test",
                OpenTelemetrySourceName = "test",
                ChatHistoryProvider = new InMemoryChatHistoryProvider(),
            },
            capabilities,
            NullLoggerFactory.Instance,
            services
        );
        var session = await agent.CreateSessionAsync(token);
        var permissions = new InteractionPermissionState(AgwPermissionMode.FullAccess);
        UserInputResponse Answer(UserInputInteraction request) =>
            new()
            {
                InteractionId = request.InteractionId,
                Cancelled = cancelled,
                ResponseData =
                    cancelled ? null
                    : toolName == "mode_set" ? JsonSerializer.SerializeToElement(new { confirmed = true })
                    : JsonSerializer.SerializeToElement(
                        new { answers = new Dictionary<string, string> { ["Database?"] = "PostgreSQL" } }
                    ),
            };
        AgentResponse finalResponse;
        if (durable)
        {
            var registry = new InteractionRequestRegistry();
            ToolApprovalRequestContent approval;
            UserInputInteraction request;
            using (ExecutionTestScopes.PushInteractions(ExecutionTestScopes.ResolvedChannel([]), registry, permissions))
            {
                var response = await agent.RunAsync("run", session, cancellationToken: token);
                approval = Assert.Single(
                    response.Messages.SelectMany(message => message.Contents).OfType<ToolApprovalRequestContent>()
                );
                request = Assert.IsType<UserInputInteraction>(
                    MafApprovalAdapter.CreateRequest(approval, "standalone", registry: registry)
                );
            }
            Assert.Null(model.Result);
            Assert.Equal(toolName == "mode_set" ? "mode-change" : "questions", request.InputKind);
            Assert.DoesNotContain("FORGED", request.Payload.GetRawText());
            Assert.DoesNotContain("FORGED", request.Arguments!.Value.GetRawText());
            var answer = Answer(request);
            session = await agent.DeserializeSessionAsync(
                await agent.SerializeSessionAsync(session, cancellationToken: token),
                cancellationToken: token
            );
            using (
                ExecutionTestScopes.PushInteractions(
                    ExecutionTestScopes.ResolvedChannel([new(request, answer)]),
                    new InteractionRequestRegistry(registry.Snapshot()),
                    permissions
                )
            )
                finalResponse = await agent.RunAsync(
                    [new ChatMessage(ChatRole.User, [MafApprovalAdapter.CreateResponse(approval, answer)])],
                    session,
                    cancellationToken: token
                );
        }
        else
        {
            var sink = new InteractionTestSink();
            var interactions = new InProcessInteractionSession(sink, AgwPermissionMode.FullAccess);
            sink.OnWrite = async (message, ct) =>
                await interactions.TrySubmitAsync(
                    Answer(Assert.IsType<UserInputInteraction>(InteractionTestData.Read(message))),
                    ct
                );
            using (
                ExecutionTestScopes.PushInteractions(interactions, interactions.Requests, interactions.PermissionState)
            )
                finalResponse = await agent.RunAsync("run", session, cancellationToken: token);
            Assert.Single(sink.Messages);
        }
        Assert.NotNull(model.Result);
        if (toolName == "mode_set")
        {
            Assert.Equal(
                cancelled ? "plan" : "execute",
                await providers[0].GetService<AgentModeProvider>()!.GetModeAsync(session, token)
            );
            if (durable && !cancelled)
            {
                Assert.Contains(
                    finalResponse.Messages,
                    message =>
                        message.AdditionalProperties?.GetValueOrDefault("type")?.ToString()
                        == AgwMessageTypes.ToolModeStatus
                );
            }
        }
        else
        {
            var result = JsonSerializer.SerializeToElement(
                model.Result,
                new JsonSerializerOptions(JsonSerializerDefaults.Web)
            );
            Assert.Equal(cancelled, result.GetProperty("cancelled").GetBoolean());
            if (!cancelled)
                Assert.Equal("PostgreSQL", result.GetProperty("answers").GetProperty("Database?").GetString());
        }
    }

    private sealed class BuiltInModel : IChatClient
    {
        private readonly FunctionCallContent _call;

        public BuiltInModel(FunctionCallContent call) => _call = call;

        public object? Result { get; private set; }

        public void Dispose() { }

        public object? GetService(Type type, object? key = null) => type.IsInstanceOfType(this) ? this : null;

        public Task<ChatResponse> GetResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            CancellationToken cancellationToken = default
        )
        {
            Result = messages
                .SelectMany(message => message.Contents)
                .OfType<FunctionResultContent>()
                .LastOrDefault()
                ?.Result;
            return Task.FromResult(
                new ChatResponse([
                    new ChatMessage(ChatRole.Assistant, Result is null ? [_call] : [new TextContent("done")])
                    {
                        MessageId = Guid.CreateVersion7().ToString("N"),
                    },
                ])
            );
        }

        public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            [EnumeratorCancellation] CancellationToken cancellationToken = default
        )
        {
            var response = await GetResponseAsync(messages, options, cancellationToken);
            foreach (var update in response.ToChatResponseUpdates())
                yield return update;
        }
    }
}
