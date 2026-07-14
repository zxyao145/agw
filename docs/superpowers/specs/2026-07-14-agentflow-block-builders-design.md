# Agentflow Block Builders Design

## Goal

Extract the construction of `ConcurrentBlock`, `GroupChatBlock`, `HandoffBlock`, and `MagenticBlock` from `AgentflowWorkflowCompiler` into four dedicated `internal static` Builder classes without changing workflow behavior, configuration, or public APIs.

## Architecture

`AgentflowWorkflowCompiler` remains responsible for compiling the overall graph, selecting the Builder for a block node, connecting executor bindings, and selecting workflow outputs. Block-specific participant resolution and MAF workflow construction move behind these four types in `Execution/Agentflows/Builders/`:

- `ConcurrentBlockBuilder`
- `GroupChatBlockBuilder`
- `HandoffBlockBuilder`
- `MagenticBlockBuilder`

Each Builder exposes one `Build(AgentflowBlockBuildContext context)` method returning `ExecutorBinding?`. Returning `null` preserves the current behavior for missing or invalid participant configuration.

## Shared Block Infrastructure

`AgentflowBlockBuildContext` carries the data already available to the compiler: agentflow identifier, block node, ordered nodes or node lookup, persisted-agent lookup, optional session scope, optional trace context, and agent host options.

`AgentflowBlockBuildSupport` is a shared helper, not a fifth Builder. It owns behavior common to multiple block types:

- deserialize `AgentflowBlockConfig` with the existing fallback behavior;
- resolve configured participant node identifiers;
- wrap participants with the existing node scope, session, and trace behavior;
- wrap an inner MAF workflow as an `AIAgent` and bind the block executor.

The existing node-scoped agent wrapper and reusable message transformations move out of the compiler into internal shared types so Builders do not depend on private compiler members.

## Builder Responsibilities

### ConcurrentBlockBuilder

Resolve all configured participants, apply block instructions, reassign upstream assistant messages to user messages for each target participant, execute participants concurrently, aggregate all response messages, and return the custom executor binding. This preserves the explicit role conversion required because this path invokes participants directly instead of through an MAF agent host.

### GroupChatBlockBuilder

Resolve participants, construct the round-robin group chat manager with the configured maximum rounds, build the MAF group chat workflow, and return its bound block executor.

### HandoffBlockBuilder

Resolve participants, configure handoff instructions, return-to-previous behavior, and autonomous-mode settings, build the MAF handoff workflow, and return its bound block executor.

### MagenticBlockBuilder

Resolve participants, select or create the configured manager participant, configure round, stall, reset, and plan-signoff limits, build the MAF Magentic workflow, and return its bound block executor.

## Data and Error Flow

The compiler creates an `AgentflowBlockBuildContext` and dispatches to exactly one Builder based on `AgentflowNodeKind`. A Builder returns `null` when participant configuration cannot be resolved, matching the existing compiler behavior. JSON parse failures continue to produce an empty default block configuration. Exceptions raised by MAF workflow construction or execution are not intercepted or translated by this refactor.

## Testing

Add an architectural regression test that fails until all four fully qualified Builder types exist and are both `abstract` and `sealed`, which is how C# represents static classes. Keep the existing compiler behavior tests as the compatibility suite, including sequential role reassignment, concurrent role reassignment, human-gate continuation, nested workflow persistence, and graph construction.

Verification commands:

```bash
dotnet test tests/Agw.Agents.Tests/Agw.Agents.Tests.csproj --no-restore --filter "FullyQualifiedName~AgentflowWorkflowCompilerTests"
dotnet test tests/Agw.Agents.Tests/Agw.Agents.Tests.csproj --no-restore
dotnet build Agw.slnx --no-restore
```

## Scope Constraints

- Do not introduce DI registration or a common Builder interface.
- Do not change persisted block configuration or API contracts.
- Do not change runtime role, session, trace, or output semantics.
- Do not modify or restage the existing contextId worktree changes.
- Do not create a commit unless explicitly requested.
