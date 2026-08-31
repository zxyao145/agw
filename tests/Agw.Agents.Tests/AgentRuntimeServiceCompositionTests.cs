using System.Text.Json;
using Agw.Agents.Execution.Agents;
using Agw.Agents.Execution.Agents.Middleware;
using Agw.Agents.Execution.Agents.Utils;
using Agw.Agents.ExternalAgents;
using Agw.Agents.ExternalAgents.ClaudeCode;
using Agw.Agents.ExternalAgents.Pi;
using Agw.Shared.Data.Entities.Agents;
using Agw.Shared.Exceptions;
using Agw.Shared.Extensions;
using Agw.Shared.Runtime;
using Agw.Shared.Utils;
using ClaudeCodeSdk.MAF;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging.Abstractions;
using OpenAI.CodexSdk.MAF;
using PiAgentSdk;
using PiAgentSdk.MAF;

namespace Agw.Agents.Tests;

public class AgentRuntimeServiceCompositionTests
{
    [Fact]
    public void BuildInstructions_WhenPromptProvided_ReturnsPrompt()
    {
        var instructions = AgentRuntimeServiceUtil.BuildInstructions("System prompt");

        Assert.Equal("System prompt", instructions);
    }

    [Fact]
    public void BuildInstructions_WhenPromptMissing_UsesDefaultPrompt()
    {
        var instructions = AgentRuntimeServiceUtil.BuildInstructions("   ");

        Assert.Equal("You are a helpful agent.", instructions);
    }

    [Fact]
    public void MergeEnvironmentVariables_ExecutionValuesOverrideAgentDefinitionValues()
    {
        var agentVariables = new Dictionary<string, string> { ["SHARED"] = "agent", ["AGENT_ONLY"] = "agent" };
        var executionVariables = new Dictionary<string, string>
        {
            ["SHARED"] = "session",
            ["SESSION_ONLY"] = "session",
        };

        var result = AgentRuntimeServiceUtil.MergeEnvironmentVariables(agentVariables, executionVariables);

        Assert.Equal("session", result["SHARED"]);
        Assert.Equal("agent", result["AGENT_ONLY"]);
        Assert.Equal("session", result["SESSION_ONLY"]);
    }

    [Fact]
    public void MergeEnvironmentVariables_ProjectAndExecutionValuesOverrideEarlierLayers()
    {
        var agentVariables = new Dictionary<string, string>
        {
            ["SHARED"] = "agent",
            ["PROJECT_SHARED"] = "agent",
            ["AGENT_ONLY"] = "agent",
        };
        var projectVariables = new Dictionary<string, string>
        {
            ["SHARED"] = "project",
            ["PROJECT_SHARED"] = "project",
            ["PROJECT_ONLY"] = "project",
        };
        var executionVariables = new Dictionary<string, string>
        {
            ["SHARED"] = "session",
            ["SESSION_ONLY"] = "session",
        };

        var result = AgentRuntimeServiceUtil.MergeEnvironmentVariables(
            agentVariables,
            projectVariables,
            executionVariables
        );

        Assert.Equal("session", result["SHARED"]);
        Assert.Equal("project", result["PROJECT_SHARED"]);
        Assert.Equal("agent", result["AGENT_ONLY"]);
        Assert.Equal("project", result["PROJECT_ONLY"]);
        Assert.Equal("session", result["SESSION_ONLY"]);
    }

    [Fact]
    public void ExternalAgentNames_Codex_HasDefaultCodexOptions()
    {
        var codexAgent = Assert.Single(AgentNames.ExternalAgentNames, agent => agent.Name == AgentNames.Codex);

        Assert.False(string.IsNullOrWhiteSpace(codexAgent.Extra));
        var options = JsonUtil.Deserialize<CodexAIAgentOptions>(codexAgent.Extra!);

        Assert.NotNull(options);
        Assert.NotNull(options.CodexOptions);
        Assert.NotNull(options.ThreadOptions);
    }

    [Fact]
    public void ExternalAgentNames_Pi_HasSafeDefaultOptions()
    {
        var piAgent = Assert.Single(AgentNames.ExternalAgentNames, agent => agent.Name == AgentNames.Pi);

        Assert.Equal(AgentNames.PiId, piAgent.Id);
        var options = JsonUtil.Deserialize<PiAgentAIAgentOptions>(piAgent.Extra!);
        Assert.NotNull(options);
        Assert.Equal(PiProjectTrust.Deny, options.SessionOptions.ProjectTrust);
        Assert.Null(options.SessionId);
        Assert.False(options.IsResume);
    }

    [Theory]
    [InlineData("claudecode", "ClaudeCode")]
    [InlineData("CODEX", "Codex")]
    [InlineData("pi", "Pi")]
    public void ExternalAgentKindResolver_KnownExternalName_ClassifiesIgnoringCase(string name, string expected)
    {
        // Arrange
        var agent = new Agent { Type = AgentType.External, Name = name };

        // Act
        var kind = ExternalAgentKindResolver.Resolve(agent);

        // Assert
        Assert.Equal(expected, kind.ToString());
    }

    [Fact]
    public void ExternalAgentKindResolver_NonExternalOrUnknownName_ReturnsNone()
    {
        // Arrange
        var systemAgent = new Agent { Type = AgentType.System, Name = AgentNames.Pi };
        var unknownExternalAgent = new Agent { Type = AgentType.External, Name = "Unknown" };

        // Act
        var systemKind = ExternalAgentKindResolver.Resolve(systemAgent);
        var unknownKind = ExternalAgentKindResolver.Resolve(unknownExternalAgent);

        // Assert
        Assert.Equal(ExternalAgentKind.None, systemKind);
        Assert.Equal(ExternalAgentKind.None, unknownKind);
    }

    [Fact]
    public void WrapExternalAgent_UsesHistoryAdapterWithoutCreatingSdkAgent()
    {
        var historyProvider = new InMemoryChatHistoryProvider();
        var service = CreateRuntimeService(historyProvider);

        var agent = service.WrapExternalAgent(new StubAIAgent(), isBackground: false);

        Assert.NotNull(agent.GetService<ExternalAgentChatHistoryAgent>());
        Assert.NotNull(agent.GetService<StubAIAgent>());
    }

    [Fact]
    public void WrapClaudeCodeAgent_UsesSessionTrackerWithoutExternalHistoryAdapter()
    {
        var service = CreateRuntimeService(new InMemoryChatHistoryProvider());

        var agent = service.WrapClaudeCodeAgent(
            new StubAIAgent(),
            isBackground: false,
            (_, _) => ValueTask.CompletedTask
        );

        Assert.NotNull(agent.GetService<ClaudeCodeProviderSessionTrackingAgent>());
        Assert.Null(agent.GetService<ExternalAgentChatHistoryAgent>());
        Assert.NotNull(agent.GetService<StubAIAgent>());
    }

    [Fact]
    public void WrapPiAgent_DecoratesWithoutExternalHistoryOrSessionTracker()
    {
        var service = CreateRuntimeService(new InMemoryChatHistoryProvider());

        var agent = service.WrapPiAgent(new StubAIAgent(), isBackground: false);

        Assert.Null(agent.GetService<ExternalAgentChatHistoryAgent>());
        Assert.Null(agent.GetService<ClaudeCodeProviderSessionTrackingAgent>());
        Assert.NotNull(agent.GetService<StubAIAgent>());
    }

    [Fact]
    public async Task WrapPiAgent_ComposedInner_ReleasesSeparatelyOwnedPiAgentOnce()
    {
        // Arrange
        var service = CreateRuntimeService(new InMemoryChatHistoryProvider());
        var owner = new DisposableStubAIAgent();
        var composed = owner
            .AsBuilder()
            .Use(
                runFunc: static (messages, session, options, innerAgent, cancellationToken) =>
                    innerAgent.RunAsync(messages, session, options, cancellationToken),
                runStreamingFunc: static (messages, session, options, innerAgent, cancellationToken) =>
                    innerAgent.RunStreamingAsync(messages, session, options, cancellationToken)
            )
            .Build();
        Assert.IsNotAssignableFrom<IAsyncDisposable>(composed);

        // Act
        var agent = service.WrapPiAgent(composed, owner, isBackground: false);
        var disposable = Assert.IsAssignableFrom<IAsyncDisposable>(agent);
        await disposable.DisposeAsync();
        await disposable.DisposeAsync();

        // Assert
        Assert.Equal(1, owner.DisposeCount);
    }

    [Fact]
    public void BuildPiAgentAIAgentOptions_OverridesTrustedPathsReservedEnvironmentAndStaleSession()
    {
        var history = new InMemoryChatHistoryProvider();
        ValueTask<PiExtensionUiResponse> HandleAsync(
            PiExtensionUiRequest request,
            CancellationToken cancellationToken
        ) => ValueTask.FromResult(PiExtensionUiResponse.Cancel(request.Id));
        ValueTask StartedAsync(string sessionId, CancellationToken cancellationToken) => ValueTask.CompletedTask;
        Func<PiExtensionUiRequest, CancellationToken, ValueTask<PiExtensionUiResponse>> handler = HandleAsync;
        Func<string, CancellationToken, ValueTask> started = StartedAsync;
        var trustedExtension = Path.GetFullPath("/trusted/extension.ts");
        var extra = JsonUtil.Serialize(
            new PiAgentAIAgentOptions
            {
                SessionId = "stale",
                IsResume = true,
                GlobalOptions = new PiAgentOptions
                {
                    EnvironmentVariables = new Dictionary<string, string>
                    {
                        ["PI_CODING_AGENT_DIR"] = "/evil/config",
                        ["GLOBAL_ONLY"] = "global",
                    },
                },
                SessionOptions = new PiSessionOptions
                {
                    SessionDir = "/evil/sessions",
                    Extensions = ["/evil/extension.ts"],
                    EnvironmentVariables = new Dictionary<string, string>
                    {
                        ["PI_CODING_AGENT_SESSION_DIR"] = "/evil/sessions",
                        ["SESSION_ONLY"] = "session",
                    },
                },
            }
        );

        var options = AgentRuntimeService.BuildPiAgentAIAgentOptions(
            extra,
            "/safe/workspace",
            "/safe/config",
            "/safe/sessions",
            providerSessionId: null,
            isResume: true,
            new Dictionary<string, string> { ["PI_OFFLINE"] = "0", ["ANTHROPIC_API_KEY"] = "explicit" },
            history,
            handler,
            started,
            [trustedExtension],
            historyPersistenceTimeout: TimeSpan.FromSeconds(12)
        );

        Assert.NotNull(options);
        Assert.Null(options.SessionId);
        Assert.False(options.IsResume);
        Assert.Equal("/safe/workspace", options.SessionOptions.WorkingDirectory);
        Assert.Equal("/safe/sessions", options.SessionOptions.SessionDir);
        Assert.True(options.SessionOptions.NoExtensions);
        Assert.Equal([trustedExtension], options.SessionOptions.Extensions);
        Assert.Equal(TimeSpan.FromSeconds(12), options.HistoryPersistenceTimeout);
        Assert.Null(options.GlobalOptions.EnvironmentVariables);
        Assert.Equal("/safe/config", options.SessionOptions.EnvironmentVariables!["PI_CODING_AGENT_DIR"]);
        Assert.Equal("/safe/sessions", options.SessionOptions.EnvironmentVariables["PI_CODING_AGENT_SESSION_DIR"]);
        Assert.Equal("1", options.SessionOptions.EnvironmentVariables["PI_OFFLINE"]);
        Assert.Equal("0", options.SessionOptions.EnvironmentVariables["PI_TELEMETRY"]);
        Assert.Equal("explicit", options.SessionOptions.EnvironmentVariables["ANTHROPIC_API_KEY"]);
        Assert.Equal("global", options.SessionOptions.EnvironmentVariables["GLOBAL_ONLY"]);
        Assert.Equal("session", options.SessionOptions.EnvironmentVariables["SESSION_ONLY"]);
        Assert.Same(history, options.ChatHistoryProvider);
        Assert.Same(handler, options.SessionOptions.ExtensionUiHandler);
        Assert.Same(started, options.OnSessionStartedAsync);
    }

    [Fact]
    public void PiRuntimePaths_DifferentUsers_ResolveToContainedDistinctDirectories()
    {
        var dataPaths = AgwDataPaths.Resolve("/tmp/agw-tests", "/unused");

        var first = PiRuntimePaths.Create(dataPaths, "1001", "/home/test-user");
        var second = PiRuntimePaths.Create(dataPaths, "1002", "/home/test-user");

        Assert.NotEqual(first.Root, second.Root);
        Assert.Equal(Path.GetFullPath("/home/test-user/.pi/agent"), first.ConfigDirectory);
        Assert.Equal(first.ConfigDirectory, second.ConfigDirectory);
        Assert.StartsWith(first.Root, first.SessionDirectory, StringComparison.Ordinal);
        Assert.Throws<AgwException>(() => PiRuntimePaths.Create(dataPaths, "../escape", "/home/test-user"));
    }

    [Fact]
    public void DisableExternalSdkChatHistoryPersistence_ForCodex_ClearsProvider()
    {
        var historyProvider = new InMemoryChatHistoryProvider();

        var codexOptions = AgentRuntimeService.DisableExternalSdkChatHistoryPersistence(
            new CodexAIAgentOptions { ChatHistoryProvider = historyProvider }
        );

        Assert.Null(codexOptions.ChatHistoryProvider);
    }

    [Fact]
    public void BuildClaudeCodeAIAgentOptions_WhenHistoryProviderProvided_UsesSdkPersistence()
    {
        var historyProvider = new InMemoryChatHistoryProvider();

        var options = BuildClaudeCodeAIAgentOptions(
            "{}",
            workspace: null,
            providerSessionId: null,
            isResume: false,
            chatHistoryProvider: historyProvider
        );

        Assert.NotNull(options);
        Assert.Same(historyProvider, options.ChatHistoryProvider);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void BuildClaudeCodeAIAgentOptions_WhenProviderSessionProvided_MapsIsResume(bool isResume)
    {
        var providerSessionId = Guid.Parse("11111111-2222-3333-4444-555555555555");
        var configuredSessionId = Guid.Parse("aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee");
        var extra = JsonUtil.Serialize(
            new ClaudeCodeAIAgentOptions
            {
                ContinueConversation = true,
                Resume = configuredSessionId.Normalize(),
                SessionId = configuredSessionId,
            }
        );

        var options = BuildClaudeCodeAIAgentOptions(extra, workspace: "/source/workspace", providerSessionId, isResume);

        Assert.NotNull(options);
        Assert.False(options.ContinueConversation);
        Assert.True(options.IncludePartialMessages);
        Assert.Equal("/source/workspace", options.WorkingDirectory);
        if (isResume)
        {
            Assert.Equal(providerSessionId.Normalize(), options.Resume);
            Assert.Null(options.SessionId);
        }
        else
        {
            Assert.Null(options.Resume);
            Assert.Equal(providerSessionId, options.SessionId);
        }
    }

    [Fact]
    public void BuildClaudeCodeAIAgentOptions_WhenProviderSessionMissing_ClearsConfiguredSessionState()
    {
        var configuredSessionId = Guid.Parse("aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee");
        var extra = JsonUtil.Serialize(
            new ClaudeCodeAIAgentOptions
            {
                ContinueConversation = true,
                Resume = configuredSessionId.Normalize(),
                SessionId = configuredSessionId,
            }
        );

        var options = BuildClaudeCodeAIAgentOptions(extra, workspace: null, providerSessionId: null, isResume: true);

        Assert.NotNull(options);
        Assert.False(options.ContinueConversation);
        Assert.True(options.IncludePartialMessages);
        Assert.Null(options.Resume);
        Assert.Null(options.SessionId);
    }

    [Fact]
    public void ResolveExternalProviderSession_ClaudeCode_CreatesThenResumesSameSession()
    {
        var agent = new Agent { Type = AgentType.External, Name = AgentNames.ClaudeCode };

        var created = AgentRuntimeService.ResolveExternalProviderSession(
            agent,
            persistedProviderSessionId: null,
            requestedResume: true
        );
        var resumed = AgentRuntimeService.ResolveExternalProviderSession(
            agent,
            created.ProviderSessionId,
            requestedResume: false
        );

        Assert.True(created.ProviderSessionId.HasValue);
        Assert.False(created.IsResume);
        Assert.Equal(created.ProviderSessionId, resumed.ProviderSessionId);
        Assert.True(resumed.IsResume);
    }

    [Fact]
    public void ResolveExternalProviderSession_Codex_PreservesExistingBehavior()
    {
        var agent = new Agent { Type = AgentType.External, Name = AgentNames.Codex };
        var providerSessionId = Guid.Parse("22222222-3333-4444-5555-666666666666");

        var created = AgentRuntimeService.ResolveExternalProviderSession(
            agent,
            persistedProviderSessionId: null,
            requestedResume: true
        );
        var resumed = AgentRuntimeService.ResolveExternalProviderSession(
            agent,
            providerSessionId,
            requestedResume: false
        );

        Assert.Null(created.ProviderSessionId);
        Assert.False(created.IsResume);
        Assert.Equal(providerSessionId, resumed.ProviderSessionId);
        Assert.True(resumed.IsResume);
    }

    [Fact]
    public void ResolveExternalProviderSession_Pi_ResumesOnlyPersistedSession()
    {
        var agent = new Agent { Type = AgentType.External, Name = AgentNames.Pi.ToLowerInvariant() };
        var providerSessionId = Guid.Parse("33333333-4444-5555-6666-777777777777");

        var created = AgentRuntimeService.ResolveExternalProviderSession(
            agent,
            persistedProviderSessionId: null,
            requestedResume: true
        );
        var resumed = AgentRuntimeService.ResolveExternalProviderSession(
            agent,
            providerSessionId,
            requestedResume: false
        );

        Assert.Null(created.ProviderSessionId);
        Assert.False(created.IsResume);
        Assert.Equal(providerSessionId, resumed.ProviderSessionId);
        Assert.True(resumed.IsResume);
    }

    [Fact]
    public void UsesProviderSessionBinding_SupportsClaudeCodeCodexAndPiExternalAgents()
    {
        Assert.True(
            AgentRuntimeService.UsesProviderSessionBinding(
                new Agent { Type = AgentType.External, Name = AgentNames.ClaudeCode }
            )
        );
        Assert.True(
            AgentRuntimeService.UsesProviderSessionBinding(
                new Agent { Type = AgentType.External, Name = AgentNames.Codex }
            )
        );
        Assert.True(
            AgentRuntimeService.UsesProviderSessionBinding(
                new Agent { Type = AgentType.External, Name = AgentNames.Pi }
            )
        );
        Assert.False(
            AgentRuntimeService.UsesProviderSessionBinding(
                new Agent { Type = AgentType.External, Name = AgentNames.GithubCopilot }
            )
        );
        Assert.False(
            AgentRuntimeService.UsesProviderSessionBinding(
                new Agent { Type = AgentType.System, Name = AgentNames.ClaudeCode }
            )
        );
    }

    [Fact]
    public void BuildCodexAIAgentOptions_WhenWorkspaceProvided_SetsThreadWorkingDirectory()
    {
        var options = BuildCodexAIAgentOptions(
            """
            {"threadOptions":{"model":"gpt-5-codex","skipGitRepoCheck":true}}
            """,
            "D:\\source\\workspace",
            threadId: null,
            resume: false
        );

        Assert.NotNull(options);
        Assert.Equal("D:\\source\\workspace", options.ThreadOptions.WorkingDirectory);
        Assert.Equal("gpt-5-codex", options.ThreadOptions.Model);
        Assert.True(options.ThreadOptions.SkipGitRepoCheck);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void BuildCodexAIAgentOptions_WhenProviderSessionProvided_UsesThreadId(bool resume)
    {
        var threadId = Guid.Parse("22222222-2222-2222-2222-222222222222");

        var options = BuildCodexAIAgentOptions("{}", workspace: null, threadId, resume);

        Assert.NotNull(options);
        Assert.Equal(threadId, options.ThreadId);
        Assert.Equal(resume, options.IsResume);
    }

    [Fact]
    public void BuildCodexAIAgentOptions_WhenThreadStartedCallbackProvided_PreservesCallback()
    {
        ValueTask OnThreadStartedAsync(string threadId, CancellationToken cancellationToken) => ValueTask.CompletedTask;

        Func<string, CancellationToken, ValueTask> callback = OnThreadStartedAsync;

        var options = BuildCodexAIAgentOptions(
            "{}",
            workspace: null,
            threadId: null,
            resume: false,
            environmentVariables: null,
            onThreadStartedAsync: callback
        );

        Assert.NotNull(options);
        Assert.Same(callback, options.OnThreadStartedAsync);
    }

    [Fact]
    public void BuildCodexAIAgentOptions_WhenEnvironmentVariablesProvided_InheritsProcessEnvironment()
    {
        const string inheritedEnvName = "AGW_TEST_INHERITED_ENV";
        Environment.SetEnvironmentVariable(inheritedEnvName, "inherited");

        try
        {
            var options = BuildCodexAIAgentOptions(
                JsonUtil.Serialize(new CodexAIAgentOptions()),
                workspace: null,
                threadId: null,
                resume: false,
                new Dictionary<string, string> { ["AGW_TOKEN"] = "secret" }
            );

            Assert.NotNull(options);
            Assert.NotNull(options.CodexOptions.Env);
            Assert.Equal("inherited", options.CodexOptions.Env[inheritedEnvName]);
            Assert.Equal("secret", options.CodexOptions.Env["AGW_TOKEN"]);
        }
        finally
        {
            Environment.SetEnvironmentVariable(inheritedEnvName, null);
        }
    }

    [Fact]
    public void ExecutionContextIdResolver_WhenContextMissing_GeneratesNormalizedContextId()
    {
        var generatedContextId = ContextIdUtil.ResolveContextId(null);
        var suppliedContextId = ContextIdUtil.ResolveContextId(" context-1 ");

        Assert.False(string.IsNullOrWhiteSpace(generatedContextId));
        Assert.Equal(Guid.Parse(generatedContextId).Normalize(), generatedContextId);
        Assert.Equal("context-1", suppliedContextId);
    }

    private static CodexAIAgentOptions? BuildCodexAIAgentOptions(
        string extra,
        string? workspace,
        Guid? threadId,
        bool resume,
        IReadOnlyDictionary<string, string>? environmentVariables = null,
        Func<string, CancellationToken, ValueTask>? onThreadStartedAsync = null
    )
    {
        var method = typeof(AgentRuntimeService).GetMethod(
            "BuildCodexAIAgentOptions",
            System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic
        );

        Assert.NotNull(method);
        return Assert.IsType<CodexAIAgentOptions>(
            method.Invoke(null, [extra, workspace, threadId, resume, environmentVariables, onThreadStartedAsync])
        );
    }

    private static ClaudeCodeAIAgentOptions? BuildClaudeCodeAIAgentOptions(
        string extra,
        string? workspace,
        Guid? providerSessionId,
        bool isResume,
        IReadOnlyDictionary<string, string>? environmentVariables = null,
        ChatHistoryProvider? chatHistoryProvider = null
    )
    {
        var method = typeof(AgentRuntimeService).GetMethod(
            "BuildClaudeCodeAIAgentOptions",
            System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic
        );

        Assert.NotNull(method);
        return Assert.IsType<ClaudeCodeAIAgentOptions>(
            method.Invoke(
                null,
                [extra, workspace, providerSessionId, isResume, environmentVariables, chatHistoryProvider]
            )
        );
    }

    private static AgentRuntimeService CreateRuntimeService(ChatHistoryProvider historyProvider) =>
        new(
            agentAppService: null!,
            projectRuntimeFacade: null!,
            capabilityComposer: null!,
            historyProvider,
            providerSessionState: null!,
            providerSessions: null!,
            dataPaths: null!,
            fileSystemResolver: null!,
            sessionStateStore: null!,
            NullLogger<AgentRuntimeService>.Instance,
            new ObservabilityMiddleware(NullLogger<ObservabilityMiddleware>.Instance),
            new UsageTrackingMiddleware(
                providerSessionState: null!,
                usageRecorder: null!,
                NullLogger<UsageTrackingMiddleware>.Instance
            ),
            summaryService: null!,
            timeProvider: TimeProvider.System
        );

    private class StubAIAgent : AIAgent
    {
        protected override ValueTask<AgentSession> CreateSessionCoreAsync(CancellationToken cancellationToken) =>
            ValueTask.FromResult<AgentSession>(new StubAgentSession());

        protected override ValueTask<JsonElement> SerializeSessionCoreAsync(
            AgentSession session,
            JsonSerializerOptions? jsonSerializerOptions,
            CancellationToken cancellationToken
        ) => ValueTask.FromResult(JsonSerializer.SerializeToElement(new { }, jsonSerializerOptions));

        protected override ValueTask<AgentSession> DeserializeSessionCoreAsync(
            JsonElement sessionState,
            JsonSerializerOptions? jsonSerializerOptions,
            CancellationToken cancellationToken
        ) => ValueTask.FromResult<AgentSession>(new StubAgentSession());

        protected override Task<AgentResponse> RunCoreAsync(
            IEnumerable<ChatMessage> messages,
            AgentSession? session,
            AgentRunOptions? options,
            CancellationToken cancellationToken
        ) => Task.FromResult(new AgentResponse());

        protected override async IAsyncEnumerable<AgentResponseUpdate> RunCoreStreamingAsync(
            IEnumerable<ChatMessage> messages,
            AgentSession? session,
            AgentRunOptions? options,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken
        )
        {
            await Task.CompletedTask;
            yield break;
        }

        private sealed class StubAgentSession : AgentSession;
    }

    private sealed class DisposableStubAIAgent : StubAIAgent, IAsyncDisposable
    {
        public int DisposeCount { get; private set; }

        public ValueTask DisposeAsync()
        {
            DisposeCount++;
            return ValueTask.CompletedTask;
        }
    }
}
