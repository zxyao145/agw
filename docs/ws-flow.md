# Agent execution flow

Agw exposes the authenticated SignalR Hub at `/api/hubs/exec`. The Hub is a transport adapter over connection-scoped command handling and either the in-process or distributed execution provider. Detailed state ownership, extension rules, checkpoint storage, and distributed recovery are documented in the [Execution subsystem README](../src/server/Agw.Agents.Execution/README.md).

## Hub contract

Clients dispatch the polymorphic `AgentRunCommand` family through:

```text
DispatchCommand(AgentRunCommand)
```

The Hub also exposes provider/checkpoint queries and InProcess recovery operations:

```text
GetExecutionProvider() -> "InProcess" | "Distributed"
GetAgentflowCheckpoints(agentflowId) -> AgentflowCheckpointAvailability[]
FindInProcessExecution(projectId, conversationId) -> originalConnectionId | null
RecoverInProcessExecution(originalConnectionId, interrupt) -> boolean
```

Runtime, control, and lifecycle output uses one typed callback:

```text
ReceiveMessage(AgwMessage)
```

`ExecutionHub` owns no mutable execution state. `ExecutionConnectionRegistry` maps each SignalR connection ID and authenticated user ID to an `ExecutionConnection`. Each connection owns an independent dependency-injection scope and serialized command gate; `ExecutionConnectionContext` owns settings, task, workspace, target, runtime attachment, and message sink. Background output uses `IHubContext`, never a captured Hub instance.

## Commands

Commands are registered through the typed handler and JSON-discriminator seam. The current command set is:

| Command | Purpose |
| --- | --- |
| `SettingCommand` | Binds the connection to a required, client-generated, non-empty GUID `conversationId` (official clients use UUIDv7) and sets Project, environment variables, and the initial permission policy. Configuring a draft conversation does not create it. |
| `ExecCommand` | Selects an Agent or Agentflow and starts a turn. `conversationId` is required and must equal the configured conversation; distributed clients also supply a stable `executionId`, and distributed execution requires `stream=true`. |
| `InterruptCommand` | Interrupts the active in-process turn or the identified durable execution. An identified execution that is not attached must belong to the configured conversation. |
| `SetModeCommand` | Changes the mode of an Agent that supports runtime modes. |
| `SetPermissionModeCommand` | Selects the next turn's permission mode. The active turn and pending interactions keep their original snapshot; an external runtime is rebuilt before the next turn when its permission version changes. |
| `HumanResponseCommand` | Supplies a typed `response` (`kind` plus `interactionId`) for Tool approval, a workflow gate, or user input; durable commands also include `executionId`, which must belong to the configured conversation. |
| `SubscribeExecutionCommand` | Reattaches the connection to an existing durable execution of the configured conversation and resumes output after an optional cursor. An execution of another conversation is reported as not found (`4040011`). |
| `ResumeCheckpointCommand` | Starts a new Agentflow branch from one exact checkpoint occurrence of the configured conversation. |

`SettingCommand.Resume` and `ExecCommand.ResumeCheckpoint` are Server-only properties and are not part of the wire contract. Without a prior Setting command, the Server uses the built-in Project, no environment variables, and the default permission mode, and binds the connection to the conversation of its first `ExecCommand`; subscriptions and checkpoint operations require a configured conversation.

`conversationId` is the Project Conversation identity and the only conversation identity clients send. The Server resolves the conversation's `contextId` through the Projects facade by Project, conversation, and current user: an existing conversation keeps its saved `contextId`, and a new conversation receives a UUIDv7 `contextId` when its first turn is accepted. `contextId` remains the runtime continuity identity used by Agent sessions, provider sessions, traces, usage, and checkpoints; clients show it only as diagnostic data. Checkpoint queries on a conversation that is not saved yet return an empty list, the same result as for another user's conversation.

A connection runs one turn at a time. An `ExecCommand`, or a changed `SettingCommand`, that arrives while the connection's turn is still running fails with HubException `4090021` (`ExecutionBusy`), so the client knows the command did not run. Once the client has received `turn-finished`, the connection accepts the next command: the in-process provider waits for the finishing turn to release its runtime, and the distributed attachment clears its active execution before it sends `turn-finished`.

This is a breaking wire-contract change: the required `SettingCommand.conversationId`, the `FindInProcessExecution(projectId, conversationId)` signature, and the required `ExecCommand.conversationId` mean the Server and Web, Desktop, and Mobile clients must be upgraded together. Older clients are not supported by this contract.

## Permission capabilities

Query the authenticated management endpoint before offering a permission choice:

```http
GET /api/agents/permission-capabilities?type=0&id={agentId}
GET /api/agents/permission-capabilities?type=1&id={agentflowId}
```

The endpoint returns a Bens.Results envelope. After unwrapping, `supportedPermissionModes` contains `fullAccess`, `alwaysAsk`, or `allowSameArguments`; `reason` explains a restriction when present. System Agents and Claude Code support all three. Codex and Pi support only `fullAccess`. Agentflows intersect the capabilities of their Agent and nested-Agentflow targets. Foreign/missing targets are unavailable; explicitly requesting an unsupported mode fails validation.

`permission-status` messages report `activePermissionMode`, `nextPermissionMode`, and `permissionChangePending`. A change affects the next turn, including when made during a durable wait; it must not hide or auto-answer an existing approval. User input and explicit HumanGate requests still need a response under Full access.

## Turn lifecycle

Each active turn receives an immutable `RuntimeTurnContext` containing settings, task, target, Project/context/Agent identifiers, authenticated user ID, absolute Project workspace, and the transport-neutral message sink. Mutable connection state never enters `AsyncLocal`; only this per-turn snapshot does.

```mermaid
sequenceDiagram
    participant Client
    participant Hub as ExecutionHub
    participant Connection as ExecutionConnection
    participant Context as ExecutionConnectionContext
    participant Projects as Projects task resolution
    participant Runtime as Agent or Agentflow runtime

    Client->>Hub: DispatchCommand(SettingCommand)
    Hub->>Connection: dispatch serialized command
    Connection->>Context: apply immutable settings
    Client->>Hub: DispatchCommand(ExecCommand)
    Connection->>Context: validate conversationId and resolve execution
    Context->>Projects: create or validate conversation and task
    Projects-->>Context: persisted conversation/task snapshot
    Context->>Runtime: create or reuse runtime and start turn
    Runtime-->>Client: turn-start
    loop runtime output
        Runtime-->>Client: AgwMessage
    end
    Runtime-->>Client: turn-finished
```

Turn state is part of the `AgwMessage` protocol:

- `additionalProperties.type = "turn-start"` precedes runtime output.
- `interaction-request` messages carry typed requests with `kind = tool-approval | workflow-gate | user-input` and a stable `interactionId`. `HumanResponseCommand.response` must match both fields; each variant carries its own decision or response data.
- `additionalProperties.type = "turn-finished"` carries `status = completed | interrupted | failed`.
- Durable lifecycle messages also carry `executionId`; streamed messages use a stable scope so replay and checkpoint branches merge with the correct user turn.

When `stream=false`, the in-process provider buffers ordinary output until completion but forwards human-interaction control messages immediately. The distributed provider rejects non-streaming execution because a durable buffer across multiple human-interaction segments has no defined compatibility contract.

## Provider-specific recovery

`Execution:Provider` is selected once at Server startup:

- `InProcess` keeps the runtime and Human-in-the-loop state in the current process. An idle disconnected connection is disposed. A running turn may finish and persist without a subscriber, but a turn waiting for a human response is interrupted because that response can no longer arrive through the detached connection. While the original process survives, recovery operations expose whether the original connection is still executing or releasing resources and allow the same owner to stop it. They do not replay disconnected output.
- `Distributed` stores user-owned execution state, checkpoints, pending interactions, and responses in PostgreSQL. Disconnecting only detaches the current subscription. Another connection can send `SubscribeExecutionCommand` with the same authenticated user ID and replay cursor; a worker resumes runnable segments under a PostgreSQL distributed lock. Output replay uses PostgreSQL by default or Redis when configured.

After reopening an existing InProcess conversation, call `FindInProcessExecution(projectId, conversationId)` before enabling Send. The calling connection is not reported for its own turn once that turn has written `turn-finished`: its client already has the result, and a command it sends next waits on the Server until the turn releases its resources. When it returns an original connection ID, retain that ID across subsequent reconnects and poll `RecoverInProcessExecution(id, false)` until it returns `false`. To stop the work, call it with `interrupt=true` and continue checking until resource cleanup finishes. Foreign and missing connections both return `false` without affecting another user's work. Query failures keep recovery pending with a retry option; loading history or receiving a model diagnostic is not proof of completion. Only `turn-finished` or a successful idle result clears the active state. Recovery does not survive a Server restart.

Distributed execution is at-least-once, not exactly-once. A Tool with external side effects must use `executionId`, request identity, or a business idempotency key. There is no separate active-execution REST lifecycle; start, subscribe, interrupt, human response, and checkpoint resume remain Hub operations.

## Agentflow checkpoint branches

Checkpoint markers emit visible `agentflow-checkpoint` messages and persist occurrence metadata. `GetAgentflowCheckpoints` reports whether each occurrence is still resumable. `ResumeCheckpointCommand` validates the authenticated user, the configured Project and conversation (through the conversation's saved `contextId`), Agentflow ID, definition fingerprint, and snapshot before removing history after the saved boundary and starting the new branch. In-process occurrences require the original runtime to remain alive; distributed occurrences survive reconnects and Server restarts.

## Client input queue

Web and Desktop keep one in-memory input queue per `serverId + projectId + conversationId` in `ExecutionSessionManager`. While a turn runs the composer stays editable; a submission joins the queue, and the queue sends its head only after a `turn-finished` with `status = completed` whose `turnId` equals the entry's `executionId`. Each entry keeps its `executionId` and message ID across resends, so the Server's turn idempotency accepts it at most once. Settings passed to the connection (Project, environment variables, permission mode, result-only) are recorded when they are handed over, and the queue sends nothing until the connection uses them: a change while the connection is idle is sent before the next entry, a change during a running turn is sent after that turn ends and before the next entry, and after a settings change fails the queue sends nothing with the previous settings; resuming the queue or submitting again retries the change. A failed or externally interrupted turn pauses the remaining queue; a `turn-finished` marked `superseded` still ends its entry's wait, and because the conversation has a newer turn started elsewhere, the remaining queue pauses. A confirmed start rejection (a HubException, or a durable `4040011` probe) returns the entry to the head for editing; an unknown outcome (a closed connection, or a reconnect that finds no active execution before the entry's `turn-start`) returns it locked to the head and pauses. A user stop clears the queue before sending `InterruptCommand`. The queue is not persisted; reloading the page or restarting the app discards it.
