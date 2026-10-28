using OpenAI.CodexSdk.AppServer;
using OpenAI.CodexSdk.AppServer.MAF;

namespace Agw.Agents.Execution.Agents.ExternalAgents.Codex;

internal sealed record CodexAgentRuntimeOptions(
    CodexAppServerClientOptions ClientOptions,
    CodexAppServerAIAgentOptions AgentOptions,
    Guid? ProviderSessionId,
    bool IsResume,
    Func<string, CancellationToken, ValueTask>? OnThreadStartedAsync
);
