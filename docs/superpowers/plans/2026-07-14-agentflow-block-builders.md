# Agentflow Block Builders Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Extract the four agentflow block construction paths into dedicated `internal static` Builder classes while preserving all workflow behavior.

**Architecture:** `AgentflowWorkflowCompiler` will create an `AgentflowBlockBuildContext` and dispatch to one of four static Builders. Shared participant resolution, block configuration, workflow wrapping, node-scoped execution, and message transforms will live in focused internal support types so no Builder depends on compiler-private members.

**Tech Stack:** C# 14, .NET 10, Microsoft.Agents.AI.Workflows 1.12.0, xUnit

## Global Constraints

- Do not introduce DI registration or a common Builder interface.
- Do not change persisted block configuration or API contracts.
- Do not change runtime role, session, trace, or output semantics.
- Do not modify or restage the existing contextId worktree changes.
- Do not create a commit unless explicitly requested.
- Use explicit constructors; do not introduce C# primary constructors.

---

### Task 1: Lock the four-Builder architecture with a failing test

**Files:**
- Modify: `tests/Agw.Agents.Tests/AgentflowWorkflowCompilerTests.cs`

**Interfaces:**
- Consumes: `Agw.Agents.Execution.Agentflows.AgentflowWorkflowCompiler`
- Produces: a structural contract for four internal static types with a static `Build` method returning `ExecutorBinding`

- [ ] **Step 1: Add the structural regression test**

Add this test to `AgentflowWorkflowCompilerTests`:

```csharp
[Fact]
public void BlockBuilders_AreDedicatedInternalStaticTypes()
{
    var assembly = typeof(AgentflowWorkflowCompiler).Assembly;
    var builderTypeNames = new[]
    {
        "Agw.Agents.Execution.Agentflows.Builders.ConcurrentBlockBuilder",
        "Agw.Agents.Execution.Agentflows.Builders.GroupChatBlockBuilder",
        "Agw.Agents.Execution.Agentflows.Builders.HandoffBlockBuilder",
        "Agw.Agents.Execution.Agentflows.Builders.MagenticBlockBuilder",
    };

    foreach (var builderTypeName in builderTypeNames)
    {
        var builderType = assembly.GetType(builderTypeName);

        Assert.NotNull(builderType);
        Assert.True(builderType.IsAbstract);
        Assert.True(builderType.IsSealed);
        Assert.False(builderType.IsPublic);
        var buildMethod = builderType.GetMethod(
            "Build",
            BindingFlags.Static | BindingFlags.NonPublic);
        Assert.NotNull(buildMethod);
        Assert.Equal(typeof(ExecutorBinding), buildMethod.ReturnType);
    }
}
```

Add `using System.Reflection;` if the test file does not already import it.

- [ ] **Step 2: Run the test and verify RED**

Run:

```bash
dotnet test tests/Agw.Agents.Tests/Agw.Agents.Tests.csproj --no-restore \
  --filter "FullyQualifiedName~BlockBuilders_AreDedicatedInternalStaticTypes" \
  --logger "console;verbosity=minimal"
```

Expected: FAIL because `ConcurrentBlockBuilder` is not found. No production file should have been created yet.

---

### Task 2: Extract shared block construction infrastructure

**Files:**
- Create: `src/server/Agw.Agents/Execution/Agentflows/Builders/AgentflowBlockBuildContext.cs`
- Create: `src/server/Agw.Agents/Execution/Agentflows/Builders/AgentflowBlockBuildSupport.cs`
- Create: `src/server/Agw.Agents/Execution/Agentflows/AgentflowMessageTransforms.cs`
- Create: `src/server/Agw.Agents/Execution/Agentflows/AgentflowNodeScopedAgent.cs`
- Modify: `src/server/Agw.Agents/Execution/Agentflows/AgentflowWorkflowCompiler.cs`

**Interfaces:**
- Produces: `AgentflowBlockBuildContext`, `AgentflowBlockConfig`, `AgentflowBlockBuildSupport`, `AgentflowMessageTransforms`, and `AgentflowNodeScopedAgent`
- Consumes: existing `AgentflowAgentSessionScope`, `AgentflowExecutionTraceContext`, `AgentflowNode`, persisted `AIAgent` instances, and `AIAgentHostOptions`

- [ ] **Step 1: Add the immutable Builder context and block configuration**

Create `AgentflowBlockBuildContext.cs` with these exact contracts:

```csharp
using Agw.Agents.Execution.Agentflows.Observability;
using Agw.Shared.Data.Entities.Agents;

using Microsoft.Agents.AI;
using Microsoft.Agents.AI.Workflows;

namespace Agw.Agents.Execution.Agentflows.Builders;

internal sealed class AgentflowBlockBuildContext
{
    public AgentflowBlockBuildContext(
        Guid agentflowId,
        AgentflowNode blockNode,
        IReadOnlyDictionary<string, AgentflowNode> nodeMap,
        IReadOnlyDictionary<string, AIAgent> nodeIdToAgent,
        AgentflowAgentSessionScope? sessionScope,
        AgentflowExecutionTraceContext? executionTraceContext,
        AIAgentHostOptions agentHostOptions)
    {
        AgentflowId = agentflowId;
        BlockNode = blockNode;
        NodeMap = nodeMap;
        NodeIdToAgent = nodeIdToAgent;
        SessionScope = sessionScope;
        ExecutionTraceContext = executionTraceContext;
        AgentHostOptions = agentHostOptions;
    }

    public Guid AgentflowId { get; }
    public AgentflowNode BlockNode { get; }
    public IReadOnlyDictionary<string, AgentflowNode> NodeMap { get; }
    public IReadOnlyDictionary<string, AIAgent> NodeIdToAgent { get; }
    public AgentflowAgentSessionScope? SessionScope { get; }
    public AgentflowExecutionTraceContext? ExecutionTraceContext { get; }
    public AIAgentHostOptions AgentHostOptions { get; }
}

internal sealed record AgentflowBlockConfig
{
    public string[]? ParticipantNodeIds { get; init; }
    public string? ManagerNodeId { get; init; }
    public int? MaxRounds { get; init; }
    public int? MaxStalls { get; init; }
    public int? MaxResets { get; init; }
    public bool? RequirePlanSignoff { get; init; }
    public string? HandoffInstructions { get; init; }
    public bool? EnableReturnToPrevious { get; init; }
    public bool? Autonomous { get; init; }
    public int? AutonomousTurnLimit { get; init; }
    public string? ContinuationPrompt { get; init; }
}
```

- [ ] **Step 2: Extract reusable message transforms**

Create `AgentflowMessageTransforms.cs`. Move the current instruction application and portable-content role reassignment unchanged:

```csharp
using Microsoft.Extensions.AI;

namespace Agw.Agents.Execution.Agentflows;

internal static class AgentflowMessageTransforms
{
    public static List<ChatMessage> ApplyInstructions(
        IReadOnlyList<ChatMessage> messages,
        string? instructions)
    {
        if (string.IsNullOrWhiteSpace(instructions))
        {
            return messages.ToList();
        }

        var result = new List<ChatMessage>
        {
            new(ChatRole.System, instructions) { AuthorName = "agw" },
        };
        result.AddRange(messages);
        return result;
    }

    public static List<ChatMessage> ReassignOtherAgentsAsUsers(
        IReadOnlyList<ChatMessage> messages,
        string targetAgentName)
    {
        return messages.Select(message =>
        {
            if (message.Role != ChatRole.Assistant
                || string.Equals(message.AuthorName, targetAgentName, StringComparison.Ordinal)
                || message.Contents.Any(content =>
                    content is not TextContent
                        and not DataContent
                        and not UriContent
                        and not UsageContent))
            {
                return message;
            }

            var reassignedMessage = message.Clone();
            reassignedMessage.Role = ChatRole.User;
            return reassignedMessage;
        }).ToList();
    }
}
```

- [ ] **Step 3: Extract the node-scoped wrapper**

Create `AgentflowNodeScopedAgent.cs` as `internal sealed class AgentflowNodeScopedAgent : DelegatingAIAgent`. Move the current `NodeScopedAgent` fields, explicit constructor, `RunCoreAsync`, `RunCoreStreamingAsync`, activity handling, and session preparation unchanged. Replace both calls to the compiler-private helper with:

```csharp
var input = AgentflowMessageTransforms.ApplyInstructions(messages.ToList(), _instructions);
```

The constructor contract must remain:

```csharp
public AgentflowNodeScopedAgent(
    AIAgent innerAgent,
    string nodeId,
    string? name,
    string? instructions,
    AgentflowAgentSessionScope? sessionScope,
    AgentflowExecutionTraceContext? executionTraceContext = null,
    Guid? agentflowId = null,
    string? traceNodeId = null,
    Guid? agentId = null)
```

- [ ] **Step 4: Add shared participant/configuration support**

Create `AgentflowBlockBuildSupport.cs` with the following API:

```csharp
internal static class AgentflowBlockBuildSupport
{
    public static AgentflowBlockConfig ReadConfig(AgentflowNode node);

    public static IReadOnlyList<(string NodeId, AIAgent Agent)>? ResolveParticipants(
        AgentflowBlockBuildContext context);

    public static AIAgent? CreateParticipant(
        AgentflowBlockBuildContext context,
        string participantNodeId,
        string runtimeNodeId);

    public static ExecutorBinding BindWorkflow(
        AgentflowBlockBuildContext context,
        Workflow workflow);
}
```

Implement `ReadConfig` with `JsonSerializer.Deserialize<AgentflowBlockConfig>` and the existing empty/default fallback on blank JSON or `JsonException`. `ResolveParticipants` must preserve configured order, remove duplicate IDs with `StringComparer.Ordinal`, return `null` for an empty list or any unresolved node/agent, and wrap each participant through `CreateParticipant` with runtime ID `$"{context.BlockNode.NodeId}.{participantNode.NodeId}"`.

`CreateParticipant` must create `AgentflowNodeScopedAgent` and enable tracing only for `AgentflowNodeKind.Agent`, matching the current `CreateBlockParticipantAgent`. `BindWorkflow` must call `workflow.AsAIAgent(... includeWorkflowOutputsInResponse: true)`, wrap that agent in `AgentflowNodeScopedAgent`, and call `BindAsExecutor(context.AgentHostOptions)`.

- [ ] **Step 5: Point non-block compiler paths at the extracted shared types**

In `AgentflowWorkflowCompiler.cs`:

```csharp
new AgentflowNodeScopedAgent(...)
```

replaces both ordinary `NodeScopedAgent` constructions. Prompt adapters call:

```csharp
BindChatTransform(
    node.NodeId,
    messages => AgentflowMessageTransforms.ApplyInstructions(messages, node.Instructions))
```

`GetBlockParticipantNodeIds` calls `AgentflowBlockBuildSupport.ReadConfig(node)`. Remove the nested `NodeScopedAgent`, compiler-private message transform methods, and nested `AgentflowBlockConfig` only after all references compile.

- [ ] **Step 6: Verify the compatibility suite remains green before moving block logic**

Run:

```bash
dotnet test tests/Agw.Agents.Tests/Agw.Agents.Tests.csproj --no-restore \
  --filter "FullyQualifiedName~AgentflowWorkflowCompilerTests" \
  --logger "console;verbosity=minimal"
```

Expected: the new structural test still FAILS because the four Builders do not exist yet; all pre-existing compiler behavior tests PASS.

---

### Task 3: Implement the four dedicated static Builders and simplify the compiler

**Files:**
- Create: `src/server/Agw.Agents/Execution/Agentflows/Builders/ConcurrentBlockBuilder.cs`
- Create: `src/server/Agw.Agents/Execution/Agentflows/Builders/GroupChatBlockBuilder.cs`
- Create: `src/server/Agw.Agents/Execution/Agentflows/Builders/HandoffBlockBuilder.cs`
- Create: `src/server/Agw.Agents/Execution/Agentflows/Builders/MagenticBlockBuilder.cs`
- Modify: `src/server/Agw.Agents/Execution/Agentflows/AgentflowWorkflowCompiler.cs`

**Interfaces:**
- Consumes: `AgentflowBlockBuildContext` and `AgentflowBlockBuildSupport`
- Produces: four `internal static` types, each with `internal static ExecutorBinding? Build(AgentflowBlockBuildContext context)`

- [ ] **Step 1: Implement ConcurrentBlockBuilder**

Move the current concurrent execution logic into:

```csharp
internal static class ConcurrentBlockBuilder
{
    internal static ExecutorBinding? Build(AgentflowBlockBuildContext context)
    {
        var participants = AgentflowBlockBuildSupport.ResolveParticipants(context);
        if (participants == null)
        {
            return null;
        }

        async ValueTask<List<ChatMessage>> RunAsync(
            List<ChatMessage> messages,
            CancellationToken cancellationToken)
        {
            var input = AgentflowMessageTransforms.ApplyInstructions(
                messages,
                context.BlockNode.Instructions);
            var tasks = participants.Select(participant => participant.Agent.RunAsync(
                AgentflowMessageTransforms.ReassignOtherAgentsAsUsers(
                    input,
                    participant.Agent.Name ?? participant.Agent.Id),
                cancellationToken: cancellationToken));
            var responses = await Task.WhenAll(tasks).ConfigureAwait(false);
            return responses.SelectMany(response => response.Messages).ToList();
        }

        return ((Func<List<ChatMessage>, CancellationToken, ValueTask<List<ChatMessage>>>)RunAsync)
            .BindAsExecutor<List<ChatMessage>, List<ChatMessage>>(
                context.BlockNode.NodeId,
                ExecutorOptions.Default,
                threadsafe: true);
    }
}
```

- [ ] **Step 2: Implement GroupChatBlockBuilder**

Its `Build` method resolves participants, reads config, computes `Math.Max(1, config.MaxRounds ?? 10)`, constructs the existing `RoundRobinGroupChatManager`, builds the MAF group chat workflow, and returns:

```csharp
return AgentflowBlockBuildSupport.BindWorkflow(context, workflow);
```

- [ ] **Step 3: Implement HandoffBlockBuilder**

Its `Build` method resolves participants and preserves all current settings:

```csharp
var builder = AgentWorkflowBuilder.CreateHandoffBuilderWith(participants[0].Agent)
    .AddParticipants(participants.Skip(1).Select(participant => participant.Agent));

if (!string.IsNullOrWhiteSpace(config.HandoffInstructions))
{
    builder = builder.WithHandoffInstructions(config.HandoffInstructions);
}

if (config.EnableReturnToPrevious == true)
{
    builder = builder.EnableReturnToPrevious();
}

if (config.Autonomous == true)
{
    var participantAgents = participants.Select(participant => participant.Agent).ToList();
    builder = builder.WithAutonomousMode(
        config.AutonomousTurnLimit,
        config.ContinuationPrompt,
        participantAgents,
        null!,
        null!);
}

return AgentflowBlockBuildSupport.BindWorkflow(context, builder.Build());
```

- [ ] **Step 4: Implement MagenticBlockBuilder**

Its `Build` method resolves participants, defaults the manager to the first participant, optionally creates the configured manager with runtime ID `$"{context.BlockNode.NodeId}.{managerNode.NodeId}.manager"`, excludes the manager ID from the team, applies `MaxRounds`, `MaxStalls`, `MaxResets`, and `RequirePlanSignoff`, then returns `AgentflowBlockBuildSupport.BindWorkflow(context, builder.Build())`.

- [ ] **Step 5: Replace compiler block construction with Builder dispatch**

Add `using Agw.Agents.Execution.Agentflows.Builders;`. Build one context for each block node:

```csharp
var blockContext = new AgentflowBlockBuildContext(
    agentflowId,
    node,
    orderedNodes.ToDictionary(item => item.NodeId, StringComparer.Ordinal),
    nodeIdToAgent,
    sessionScope,
    executionTraceContext,
    AgentHostOptions);
```

Dispatch in `CreateBinding`:

```csharp
AgentflowNodeKind.ConcurrentBlock => ConcurrentBlockBuilder.Build(blockContext),
AgentflowNodeKind.GroupChatBlock => GroupChatBlockBuilder.Build(blockContext),
AgentflowNodeKind.HandoffBlock => HandoffBlockBuilder.Build(blockContext),
AgentflowNodeKind.MagenticBlock => MagenticBlockBuilder.Build(blockContext),
```

Remove `CreateConcurrentBlockBinding`, `CreateBlockAgent`, `BuildHandoffBlock`, `BuildGroupChatBlock`, `BuildMagenticBlock`, `CreateBlockParticipantAgent`, and `CreateBlockWorkflowAgent` from the compiler.

- [ ] **Step 6: Run the structural test and verify GREEN**

Run the Task 1 command again.

Expected: PASS; every discovered Builder is internal, abstract, sealed, and exposes the required static `Build` method.

- [ ] **Step 7: Run the complete compiler behavior suite**

Run:

```bash
dotnet test tests/Agw.Agents.Tests/Agw.Agents.Tests.csproj --no-restore \
  --filter "FullyQualifiedName~AgentflowWorkflowCompilerTests" \
  --logger "console;verbosity=minimal"
```

Expected: all compiler tests PASS, including concurrent upstream-role reassignment.

---

### Task 4: Final verification and worktree audit

**Files:**
- Verify only; do not add production scope.

**Interfaces:**
- Consumes: the extracted Builder implementation
- Produces: evidence that the refactor builds and preserves Agents behavior

- [ ] **Step 1: Run all Agents tests**

```bash
dotnet test tests/Agw.Agents.Tests/Agw.Agents.Tests.csproj --no-restore \
  --logger "console;verbosity=minimal"
```

Expected: PASS.

- [ ] **Step 2: Build the solution**

```bash
dotnet build Agw.slnx --no-restore --verbosity:minimal
```

Expected: build succeeds with no new errors. Existing NuGet source and dependency vulnerability warnings may remain.

- [ ] **Step 3: Audit the diff without staging**

```bash
git diff --check
git status --short
git diff -- src/server/Agw.Agents/Execution/Agentflows tests/Agw.Agents.Tests/AgentflowWorkflowCompilerTests.cs
```

Expected: no whitespace errors; changes are limited to the approved Builder extraction, its tests, spec, and plan. Existing staged contextId changes remain staged exactly as they were. Do not run `git add` or `git commit`.
