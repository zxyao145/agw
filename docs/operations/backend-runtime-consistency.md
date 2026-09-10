# Runtime consistency and maintenance

This guide covers durable execution ownership, conversation reset, and recovery. See [Deployment](../4.Deployment.md) for Host configuration and [conversation persistence](conversation-persistence.md) for history/event write intervals.

## Upgrade prerequisites

Apply the selected database provider's pending migrations through the deployment's database-change process before starting an already initialized Server. Matching SQLite and PostgreSQL migrations exist; use only the set for that deployment. The [Development Guide](../1.Development.md) contains the commands.

| Migration | Runtime dependency |
| --- | --- |
| `AddDurableExecutionScope` | Adds `project_id`, `project_conversation_id`, `scope_backfilled`, and scope indexes to `durable_execution`. |
| `ConversationSessionGeneration` | Adds `project_conversation.generation`, initially `0`. |
| `RenameTaskSessionBindingToProjectConversationBinding` | Moves external SDK session bindings to the `project_conversation_binding` table. |

Back up the database, `server-state.json`, and Data Protection keys together. Upgrade Server and clients together for the typed interaction and recovery Hub contracts. Old pending interaction payloads have no compatibility migration; complete or interrupt those executions before upgrading. For split deployments, initialize Control Plane before starting Data Plane with the same database, keys, and host-visible Workspace paths.

## Execution ownership

Workers retain the `StateVersion` returned when claiming a segment. Lock loss or a changed claim cancels that attempt, and the original version prevents a late result from overwriting a newer claim. An old worker must not replace its claim version with a fresh database read.

This protects state commits, while external Tool effects remain at-least-once. When inspecting a recovered execution, distinguish a repeated external effect from a stale attempt successfully committing a result.

Legacy durable rows receive indexed Project/Conversation scope through the existing recovery service. Unreadable manifests or inconsistent scope are handled under the execution lock and concurrency version. Inspect scope-recovery logs and preserve the database/key backup when investigating quarantined work; do not guess ownership or rewrite encrypted manifests manually.

## Conversation reset

Reset advances `Generation` and clears history, traces, checkpoints, provider bindings, and SDK session state in one transaction. The Conversation ID, ContextId, title, and usage remain. InProcess starts and resets share a conversation execution lease; active durable work blocks reset.

History, session, and checkpoint callbacks retain the generation captured by their original execution. `SaveConversationChangesAsync` validates the owner and generation with Project → Conversation lock ordering before committing children. A delayed callback cannot revive a cleared conversation by reading and adopting its new generation.

When reset is blocked, inspect or stop the active execution and wait for cleanup before retrying. For InProcess, a new client can use `FindInProcessExecution` and `RecoverInProcessExecution` while the original Server process is alive. Distributed clients subscribe or interrupt by execution ID. The [Hub contract](../ws-flow.md) defines those operations and their ownership checks.

## Verification

Run from the repository root:

```bash
dotnet test tests/Agw.Agents.Tests --filter "FullyQualifiedName~DurableExecutionScopeMaintenanceTests|FullyQualifiedName~DurableExecutionScopeRecoveryServiceTests|FullyQualifiedName~RuntimeDefinitionRefreshTests"
dotnet test tests/Agw.Projects.Tests --filter "FullyQualifiedName~ProjectConversationAppServiceTests"
```

Real PostgreSQL fencing verification is opt-in:

```bash
dotnet test tests/Agw.Agents.Tests --filter "FullyQualifiedName~PostgresExecutionFencingTests"
```

Supply `AGW_TEST_POSTGRES_CONNECTION_STRING` for an isolated PostgreSQL instance with CREATE DATABASE permission. The fencing test creates and drops its own temporary database and simulates advisory-lock connection loss; without the variable, it is skipped. Passing SQLite tests alone does not validate PostgreSQL lock behavior.
