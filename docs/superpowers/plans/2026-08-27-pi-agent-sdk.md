# PiAgentSdk + PiAgentSdk.MAF Implementation Plan

**Goal:** Add a .NET SDK (`PiAgentSdk`) that drives the Pi coding agent through RPC mode, add a MAF adapter (`PiAgentSdk.MAF`) that exposes Pi as `Microsoft.Agents.AI.AIAgent`, and integrate it into Agw as the third external agent beside Claude Code and Codex.

**Architecture:** Keep the SDK independent of Agw. `PiAgentSdk` owns process lifecycle and the LF-framed JSONL RPC protocol. `PiAgentSdk.MAF` maps that protocol to MAF sessions, messages, usage, errors, and history hooks. `Agw.Agents` owns user-scoped runtime configuration, history adaptation, human interaction, provider-session binding, and middleware composition.

**Target:** .NET 10.0, C# 14, Microsoft.Agents.AI 1.15.0, xUnit v3, and `@earendil-works/pi-coding-agent` RPC mode.

## Success Criteria

- A caller can start, stream, abort, dispose, and resume a persistent Pi session without probing the CLI during object construction.
- Every RPC command either completes, times out, is cancelled, or fails when the process exits; no pending request can hang indefinitely.
- A cancelled or prematurely disposed run is drained to `agent_settled`, or the process is killed and the current `PiSession` becomes unusable.
- A persistent session whose process was killed can be recovered with `ResumeSession(sessionId, originalOptions)`.
- MAF receives non-duplicated text, reasoning, Tool calls/results, errors, and accumulated usage.
- Agw persists only request messages and authoritative `turn_end` history, never raw text deltas.
- Agw preserves cross-Agent handoff context and bridges foreground Extension UI from server-allowlisted extensions; background dialogs are cancelled.
- Agw reuses the host `~/.pi/agent` auth/settings/models configuration while keeping Session files below the authenticated user's Agw data directory, with `--no-approve`, `--no-extensions`, and a sanitized environment by default.
- `dotnet build Agw.slnx`, `dotnet test Agw.slnx`, formatting checks, and diff checks pass.

## Global Constraints

- Projects live under `src/sdks/pi-agent-sdk-csharp/` in the Agw repository.
- The SDK subtree owns its `Directory.Build.props` and `Directory.Packages.props`; `src/server/Directory.Packages.props` does not apply to it.
- Use explicit constructors and fields. Do not use primary constructors.
- Constructors must not resolve, invoke, or probe the real Pi CLI.
- Default tests must not invoke the real Pi CLI. Use pure protocol tests, an in-memory transport, or a fake RPC process.
- A real CLI suite is opt-in and gated by configuration plus executable availability.
- Pi RPC is the only integration mode in v1.
- Do not add database migrations; the existing provider-session binding stores the Pi Session ID.
- Do not implement steer, follow-up, direct RPC bash, fork, clone, tree, or dynamic MAF Tool injection in v1.
- Do not automatically install or update Pi.
- Do not stage or commit changes unless the user separately authorizes it.
- Preserve unrelated working-tree changes.

## Resolved Decisions

| Area | Decision |
|---|---|
| Completion boundary | `agent_settled`, never `agent_end` |
| Session identity | Wire and SDK use `string`; Agw persists the raw string, but its current shared runtime plumbing parses Pi 0.84.4 UUIDv7 IDs through `Guid` |
| Concurrent runs | One active run per `PiSession`; a second run fails immediately |
| Project trust | Default `--no-approve` |
| Extension UI | Full foreground bridge for explicitly loaded trusted extensions; background dialog requests auto-cancel |
| History | `turn_end` is authoritative; deltas are display-only and never persisted |
| Agw history adapter | Dedicated `PiChatHistoryProvider` |
| Agw wrapper | `WrapPiAgent` decorates directly; the final `ResourceOwningAIAgent` retains and disposes `PiAgentAIAgent` |
| Pi config | Reuse host `~/.pi/agent`; do not create an Agw-owned config copy |
| Session directory | Public SDK exposes `SessionDir`; Agw forcibly replaces both CLI and env channels with one trusted path |
| Command timeout | Configurable; 30 seconds by default |
| Abort grace | Configurable; 5 seconds by default |
| Forced kill | Current object is faulted; persistent Session can be reopened with `ResumeSession(id)` |
| Startup network | `PI_OFFLINE=1`, `PI_SKIP_VERSION_CHECK=1`, `PI_TELEMETRY=0` in Agw |
| Compatibility baseline | Confirmed initial baseline is 0.84.4; rerun protocol compatibility checks before any upgrade |

## Implemented Key Layout

The tree below lists the implementation entry points; project files, assembly metadata, and individual test files are omitted.

```text
src/sdks/pi-agent-sdk-csharp/
├── Directory.Build.props
├── Directory.Packages.props
├── README.md
├── src/
│   ├── PiAgentSdk/
│   │   ├── README.md
│   │   ├── PiAgent.cs
│   │   ├── PiAgentOptions.cs
│   │   ├── PiImage.cs
│   │   ├── PiSession.cs
│   │   ├── PiSessionOptions.cs
│   │   ├── PiTurn.cs
│   │   ├── Exceptions.cs
│   │   ├── Protocol/
│   │   │   ├── PiCommands.cs
│   │   │   ├── PiEvents.cs
│   │   │   ├── PiMessages.cs
│   │   │   ├── PiExtensionUi.cs
│   │   │   └── PiProtocolJson.cs
│   │   └── Internal/
│   │       ├── IPiProcessTransport.cs
│   │       ├── PiProcessTransport.cs
│   │       ├── PiJsonlReader.cs
│   │       ├── PiRpcConnection.cs
│   │       ├── PiProcessArguments.cs
│   │       ├── PiProcessEnvironment.cs
│   │       └── PiProcessTarget.cs
│   └── PiAgentSdk.MAF/
│       ├── README.md
│       ├── PiAgentAIAgent.cs
│       ├── PiAgentAIAgentOptions.cs
│       ├── PiAgentSession.cs
│       └── Internal/
│           ├── PiAgentSessionJson.cs
│           ├── PiEventMapper.cs
│           └── PiMafPromptBuilder.cs
└── tests/
    ├── PiAgentSdk.Tests/
    └── PiAgentSdk.MAF.Tests/

src/server/Agw.Agents/ExternalAgents/
├── ExternalAgentKind.cs
└── Pi/
    ├── PiChatHistoryProvider.cs
    ├── PiExtensionUiBridge.cs
    ├── PiExternalAgentOptions.cs
    └── PiRuntimePaths.cs
```

---

## Task 1: Version Preflight and Project Wiring

### Goal

Confirm the Pi protocol baseline, create the four SDK/test projects, and add them to the existing solution without implementing runtime behavior.

### Files

- Create the subtree build/package props and four project files under `src/sdks/pi-agent-sdk-csharp/`.
- Modify `src/server/Agw.Agents/Agw.Agents.csproj` to reference `PiAgentSdk.MAF`.
- Modify `Agw.slnx`; use `/6.SDKs/` so it does not collide with the existing `/5.Shared/` folder.

### Steps

1. Run the version preflight:

   ```bash
   npm view @earendil-works/pi-coding-agent version
   ```

2. If the version is `0.84.4`, record it as the initial compatibility baseline. If it differs, compare that version's official RPC types, framing rules, message types, and Extension UI protocol before writing fixtures.
3. Create `PiAgentSdk` with no MAF dependency.
4. Create `PiAgentSdk.MAF` referencing `PiAgentSdk` and Microsoft.Agents.AI 1.15.0.
5. Create the two xUnit v3 projects. Core internals are visible to the MAF adapter and both SDK test assemblies; MAF internals are visible only to its test assembly.
6. Add all four projects to `Agw.slnx` without reordering existing projects.

### Verification

```bash
dotnet restore Agw.slnx
dotnet build Agw.slnx
```

### Acceptance

- The solution restores and builds with empty SDK projects.
- The recorded Pi baseline comes from npm/package metadata, not only the documentation website.
- No real Pi process is started.

---

## Task 2: Protocol Contracts and Strict JSONL Framing

### Goal

Model all protocol data needed by the SDK and implement an LF-only JSONL reader that remains compatible with new Pi event/message variants.

### Public Contracts

Model:

- User, Assistant, Tool Result, and bash-execution messages.
- Text, image, thinking, and Tool Call content.
- Custom, branch-summary, and compaction-summary messages.
- Usage and cost, including input, output, cache-read, cache-write, total tokens, and total cost.
- Lifecycle, message, Tool execution, retry, compaction, queue, extension-error, and `agent_settled` events.
- `stopReason`, `errorMessage`, nullable exit code, session state, and Session file.
- Extension UI request/response shapes.
- `PiUnknownEvent`, `PiUnknownMessage`, and `PiUnknownContent`, each preserving raw JSON.

Use explicit JSON converters when an abstract polymorphic hierarchy cannot safely preserve an unknown discriminator. A new Pi discriminator must not make the entire line disappear.

### Framing Rules

- Split records only on byte `0x0A` (`\n`).
- Strip one trailing `\r` immediately before the LF.
- Do not treat a lone CR, U+2028, or U+2029 as a delimiter.
- Buffer record bytes across arbitrary reads, then decode each complete record with strict UTF-8 so a multi-byte character may span reads without corruption.
- Reject or fault an oversized unterminated record with a protocol exception; use a documented finite maximum.

### Failing Tests First

- Known event and message fixtures deserialize.
- `assistant.errorMessage` and `stopReason=error` survive deserialization.
- Tool-result images and nullable bash exit codes survive deserialization.
- Custom/summary variants are recognized.
- Unknown event/message/content discriminators produce raw fallback objects.
- Multiple records in one buffer are returned separately.
- A record split across arbitrary byte chunks is reconstructed.
- CRLF is accepted; a lone CR is not a record separator.
- U+2028/U+2029 remain inside JSON strings.
- Oversized incomplete records fail deterministically.

### Verification

```bash
dotnet test src/sdks/pi-agent-sdk-csharp/tests/PiAgentSdk.Tests --filter "FullyQualifiedName~ProtocolTests|FullyQualifiedName~JsonlReaderTests"
```

---

## Task 3: Options, Arguments, Environment, and CLI Resolution

### Goal

Define configuration and build a deterministic process invocation without inheriting arbitrary host secrets.

### Public API

```csharp
public sealed record PiAgentOptions
{
    public string? PiPathOverride { get; init; }
    public TimeSpan CommandTimeout { get; init; } = TimeSpan.FromSeconds(30);
    public TimeSpan AbortGracePeriod { get; init; } = TimeSpan.FromSeconds(5);
    public IReadOnlyDictionary<string, string>? EnvironmentVariables { get; init; }
}

public enum PiProjectTrust
{
    Deny,
    Approve,
}

public sealed record PiSessionOptions
{
    public string? WorkingDirectory { get; init; }
    public string? Provider { get; init; }
    public string? Model { get; init; }
    public string? ThinkingLevel { get; init; }
    public string? SessionDir { get; init; }
    public string? SessionName { get; init; }
    public bool NoSession { get; init; }
    public PiProjectTrust ProjectTrust { get; init; } = PiProjectTrust.Deny;
    public IReadOnlyList<string>? Tools { get; init; }
    public IReadOnlyList<string>? ExcludeTools { get; init; }
    public bool NoExtensions { get; init; }
    public IReadOnlyList<string>? Extensions { get; init; }
    public IReadOnlyDictionary<string, string>? EnvironmentVariables { get; init; }
    public Func<PiExtensionUiRequest, CancellationToken, ValueTask<PiExtensionUiResponse>>?
        ExtensionUiHandler { get; init; }
}
```

Callbacks and runtime services must be ignored by JSON configuration serialization.

### Validation

- `CommandTimeout` and `AbortGracePeriod` must be positive.
- `ResumeSession` requires a nonblank ID and forbids `NoSession=true`.
- MAF/provider-session callbacks are invalid with `NoSession=true`.
- Empty Tool names, empty explicit extension paths, and unsupported thinking levels fail before process start.
- Validation may run during SDK/session creation, but CLI resolution remains lazy.

### Argument Rules

- Always emit `--mode rpc`.
- `PiProjectTrust.Deny` emits `--no-approve`; `Approve` emits `--approve`.
- A nonblank `SessionDir` emits `--session-dir <path>`.
- A resume ID emits `--session <id>` only for persistent sessions.
- `NoExtensions=true` emits `--no-extensions`.
- Each explicit `Extensions` path emits a separate `--extension`; explicit paths remain loadable when discovery is disabled.
- Use `ProcessStartInfo.ArgumentList`; never compose a shell command string.

Pi precedence is documented as:

```text
--session-dir > PI_CODING_AGENT_SESSION_DIR > Pi default
```

### Sanitized Environment

Build the base environment from exact-name allowlists plus one locale-prefix rule.

Exact process keys:

```text
PATH HOME USER LOGNAME SHELL TMPDIR TMP TEMP TZ LANG
SystemRoot ComSpec PATHEXT USERPROFILE APPDATA LOCALAPPDATA
```

Locale keys are copied by iterating the real environment and applying:

```csharp
key.StartsWith("LC_", StringComparison.Ordinal)
```

Do not treat `LC_*` as a literal environment key.

Proxy keys, with both cases:

```text
HTTP_PROXY HTTPS_PROXY ALL_PROXY NO_PROXY
http_proxy https_proxy all_proxy no_proxy
```

Certificate/trust keys:

```text
SSL_CERT_FILE SSL_CERT_DIR NODE_EXTRA_CA_CERTS
CURL_CA_BUNDLE REQUESTS_CA_BUNDLE GIT_SSL_CAINFO
NPM_CONFIG_CAFILE AWS_CA_BUNDLE
```

Environment precedence for the standalone SDK:

```text
sanitized host base
  < PiAgentOptions.EnvironmentVariables
  < PiSessionOptions.EnvironmentVariables
```

Do not inherit `NODE_OPTIONS`, provider API keys, SSH agent variables, or cloud credentials unless the caller explicitly supplies them.

### CLI Resolution

- Resolve `PiPathOverride` first, then PATH and supported install locations.
- Resolution happens only when the Session starts.
- Unix executes an executable/shebang target directly.
- Windows npm `.cmd`/`.bat` shims resolve the package-declared Pi Node entrypoint and invoke Node with `ArgumentList`; unrecognized command scripts fail closed instead of passing configuration through `cmd.exe /c`.

### Failing Tests First

- Default and custom timeouts validate.
- Default args include RPC and `--no-approve`.
- `SessionDir` emits `--session-dir` and takes documented precedence.
- Resume and `NoSession` conflicts fail.
- Proxy/certificate variables pass through.
- `LC_` keys use prefix matching.
- `NODE_OPTIONS` and host API keys do not pass through implicitly.
- Explicit Agent/Session values override ordinary allowlisted keys.
- Windows target construction is correct without invoking a real CLI.

### Verification

```bash
dotnet test src/sdks/pi-agent-sdk-csharp/tests/PiAgentSdk.Tests --filter "FullyQualifiedName~OptionsAndEnvironmentTests|FullyQualifiedName~PiProcessTargetTests"
```

---

## Task 4: Process Transport and RPC Connection

### Goal

Implement a process/connection state machine in which every command and event stream reaches a terminal state.

### Internal Boundaries

Define the smallest internal abstraction needed for two adapters:

- `PiProcessTransport`: production `Process` adapter.
- Fake/in-memory adapter: deterministic test transport.

The abstraction must support start, serialized stdin writes, stdout byte reads, bounded stderr capture, exit observation, and process-tree kill. It must not expose Agw or MAF types.

### Connection State

Serialize startup with a semaphore and guard connection/session lifecycle with atomic started, active-run, faulted, and disposed flags. Each operation validates the state it requires before acting.

### Pending Commands

- Assign a unique request ID before writing.
- Register the pending completion before the serialized write.
- Apply `CommandTimeout` independently of caller cancellation.
- Remove and complete a pending request on success, RPC failure, timeout, cancellation, write failure, process exit, kill, and Dispose.
- Dispose cancellation/timeout registrations.
- A write failure must throw/fault; it must never be swallowed after registering a request.
- The process-exit monitor faults pending commands with `PiProcessExitException`, including a zero exit code when the connection did not initiate disposal.

### Event Routing

- Responses go only to their matching pending completion.
- Regular events go to the active run's bounded Channel.
- Fire-and-forget Extension UI requests also appear in the event stream.
- Dialog Extension UI requests are dispatched on a separate task; the stdout pump never waits for user input.
- With no dialog handler, send `cancelled: true` immediately.
- stdin responses from dialog handlers share the same serialized writer as normal commands.
- Cancellation and early disposal start the internal event drain before awaiting the abort response, so a full bounded Channel cannot deadlock the multiplexed stdout pump.

### Diagnostics

- Retain only a bounded stderr tail.
- Do not log raw stdout or full stderr as routine diagnostics.
- Exceptions include exit code/signal and the bounded stderr tail.

### Failing Tests First

- Success and error responses complete only their request.
- Default and custom command timeouts are distinguishable from caller cancellation.
- Exit before `get_state` or prompt acknowledgement faults immediately.
- stdin write failure faults and removes the pending command.
- Dispose faults pending commands and completes event readers.
- Unknown responses/events do not corrupt other requests.
- Dialog handling does not block delivery of unrelated stdout records.
- Concurrent dialog and command writes remain valid JSONL records.
- More than one Channel capacity of queued events can still be aborted and drained without killing a responsive Pi process.
- stderr retention is bounded.

### Verification

```bash
dotnet test src/sdks/pi-agent-sdk-csharp/tests/PiAgentSdk.Tests --filter "FullyQualifiedName~PiProcessTransportTests|FullyQualifiedName~PiSessionTests"
```

---

## Task 5: PiAgent and PiSession Lifecycle

### Goal

Expose safe new/resume Session APIs and ensure cancellation cannot leak old events into the next turn.

### API Behavior

- `PiAgent.StartSession` constructs an unstarted persistent or ephemeral Session.
- `PiAgent.ResumeSession` constructs an unstarted persistent Session for the supplied ID.
- `PiSession.EnsureStartedAsync` starts once, sends `get_state`, requires a nonblank Session ID, and captures `SessionFile` when available.
- For resume, the Pi-reported ID must match the requested ID; mismatch is a protocol failure.
- A forced-killed or faulted `PiSession` rejects every future run.

### Run State Machine

- Acquire a fail-fast single-run gate before creating the run collector.
- Use the connection's bounded event Channel and ensure an aborted run is fully drained before the run gate is released.
- Treat the prompt response as acceptance only.
- Stream until `agent_settled`.
- Use `message_end` and `turn_end` as authoritative message boundaries; never rebuild final messages from text deltas alone.
- Release the run gate only after normal settling or cancellation cleanup completes.

### Cancellation and Early Disposal

When caller cancellation fires or the async enumerator is disposed before settlement:

1. Stop yielding to the caller.
2. Start draining the current run with an internal cleanup token.
3. Send `abort` while the drain is active, then await both its correlated response and `agent_settled`.
4. If both complete within `AbortGracePeriod`, keep the Session reusable.
5. Otherwise kill the process tree and fault the Session.
6. Propagate caller cancellation; early enumerator disposal completes normally after cleanup.

No event from an aborted run may remain available to a later run.

### Forced Kill and Resume

- A kill does not delete a persistent Pi JSONL Session.
- Retain the known Session ID for diagnostics/recovery.
- The killed `PiSession` object never restarts itself.
- A caller may create a new object with `PiAgent.ResumeSession(id, originalOptions)`.
- Recovery begins at Pi's last persisted Session entry; in-flight Tool state may be absent.
- Agw gets the same behavior by rebuilding its runtime from the stored provider-session binding.
- Ephemeral sessions cannot be resumed after kill.

### PiTurn and Usage

- Collect authoritative Assistant/Tool messages from completed turns.
- Final text comes from the last authoritative Assistant message.
- Accumulate Assistant, Tool-nested, and compaction usage exactly once.
- Do not add `message_update.usage` deltas to final totals.
- Preserve cache-write and cost metadata.
- Record a terminal agent error separately from transport/process failures.

### Failing Tests First

- Constructor and `StartSession` do not probe the CLI.
- New and resumed handshake behavior is correct.
- Resume ID mismatch fails closed.
- Concurrent runs fail immediately.
- Prompt acceptance does not finish the run.
- `agent_end` does not finish the run; `agent_settled` does.
- Caller cancellation sends abort and drains.
- Early enumerator disposal follows the same cleanup path.
- Abort timeout kills and faults the object.
- The next run never receives events from the prior run.
- A new `ResumeSession(id)` works after forced kill of a persistent Session.
- `NoSession` cannot resume.
- Multiple Assistant/Tool/compaction usages aggregate once.

### Verification

```bash
dotnet test src/sdks/pi-agent-sdk-csharp/tests/PiAgentSdk.Tests --filter "FullyQualifiedName~PiSessionTests"
```

---

## Task 6: MAF Contracts, Prompt Mapping, and Extension UI

### Goal

Define serializable MAF Session state, map Pi events without duplication, preserve current-call context, and expose foreground human interaction.

### MAF Session and Options

`PiAgentSession` contains:

- `string? SessionId`
- nonserialized live `PiSession` binding
- normal MAF `StateBag`

Serialize through options that compose the Microsoft Agent abstractions resolver with a source-generated Pi context. Round-trip a populated StateBag in tests, not only the Session ID.

`PiAgentAIAgentOptions` contains global/session options, Session ID, resume flag, session-start callback, `ChatHistoryProvider`, runtime Extension UI handler, and a positive `HistoryPersistenceTimeout` (30 seconds by default). Callback/provider/handler members are JSON-ignored.

### Prompt Builder

- Process only the messages supplied for the current invocation; ignore history returned by `ChatHistoryProvider.InvokingAsync` because Pi owns provider-side history.
- Preserve System, Assistant handoff, User, and Tool text in original order.
- Add role labels when more than one role is present.
- Do not send `TextReasoningContent` back as prompt text.
- Preserve Tool Result payloads as labeled context without treating them as new executable calls.
- Convert User `DataContent` images into Pi base64 images.
- For image-only input, send a neutral nonblank prompt.

### Event Mapping

- Text and thinking deltas become streaming MAF content.
- Only `toolcall_end` produces `FunctionCallContent`; mark it `InformationalOnly=true`.
- Preserve Tool argument types instead of calling `JsonElement.ToString()` for every value.
- Tool execution start/update do not emit repeated text; the informational Tool Call remains running until Tool execution end produces its result.
- Tool execution end becomes `FunctionResultContent`; failures include `ErrorContent`.
- Assistant `stopReason=error`, final retry failure, and compaction failure become fatal `ErrorContent` with consistent metadata.
- Caller cancellation remains cancellation and does not produce a fatal provider error.
- `turn_end` maps to complete Assistant/Tool history messages.
- If a content block produced no streaming delta, `message_end` or `turn_end` emits the missing authoritative Assistant content once.
- Malformed base64 in a known image content block becomes `PiProtocolException`, not an unclassified `FormatException`.
- Usage updates are emitted only at authoritative boundaries.

### Extension UI Core Behavior

- Model select, confirm, input, editor, notify, status, widget, title, and editor-text requests.
- Dialogs call the configured typed handler outside the stdout pump.
- The handler response ID must match the request.
- Respect the Pi-provided timeout with a linked cancellation source.
- With no handler, dialog requests return cancelled.
- Fire-and-forget requests produce System updates and never wait for responses.

### Failing Tests First

- Session ID and populated StateBag round-trip.
- Assistant handoff plus current User input both reach the prompt.
- System and Tool context preserve order.
- Private reasoning is omitted from the prompt.
- Image-only input is accepted.
- One Pi Tool Call yields one informational MAF Tool Call.
- Nested Tool arguments retain JSON types.
- Provider/retry/compaction failures are fatal; caller cancellation is not.
- All four dialogs map through the handler.
- Fire-and-forget UI maps to System updates.
- Missing, timed-out, cancelled, and mismatched UI responses cancel safely.

### Verification

```bash
dotnet test src/sdks/pi-agent-sdk-csharp/tests/PiAgentSdk.MAF.Tests
```

---

## Task 7: PiAgentAIAgent and Authoritative History Hooks

### Goal

Implement the MAF `AIAgent` lifecycle and call `ChatHistoryProvider` only with request messages and authoritative completed turns.

### Execution Flow

1. Normalize the provided `AgentSession` or create a new `PiAgentSession`.
2. Invoke the history provider for state initialization, but do not append returned history to the Pi prompt.
3. Persist request messages before starting the run by invoking the provider with requests and no responses.
4. Create or reuse the live `PiSession`; resume when `PiAgentSession.SessionId` is present.
5. After startup, validate/bind the Pi-reported ID and invoke `OnSessionStartedAsync` once for a new Session.
6. Stream mapped display updates.
7. At each `turn_end`, persist the complete Assistant/Tool messages immediately.
8. Persist each completed turn before yielding its mapped `turn_end` update, leaving no completed-turn response buffer to flush during cancellation, failure, or consumer disposal.

### History Persistence

- Persist completed requests/turns with a cleanup token independent of caller cancellation.
- Bound that independent cleanup token with `HistoryPersistenceTimeout`; a stalled provider must not block a run forever.
- A persistence failure or timeout fails the run; a timed-out provider task that ignores cancellation is observed in the background so a later exception is not unobserved.
- Never persist text deltas, partial Tool output snapshots, or duplicate Tool Call content.

### Non-streaming Response

- Return authoritative history messages.
- Return accumulated usage across all Pi turns and compaction work.
- On non-streaming compaction failure, keep usage only in `AgentResponse.Usage`; the error message contains only `ErrorContent`.
- Include fatal ErrorContent for agent-declared failure.
- Transport/process failures remain exceptions.

### Lifetime and Concurrency

- Construction and Session creation do not probe Pi.
- Guard lazy live-Session binding against races.
- Dispose every live Pi Session once.
- Middleware composition must not hide `PiAgentAIAgent` ownership; Agw retains it through a final disposable resource wrapper.
- A disposed `PiAgentAIAgent` rejects future runs.

### Failing Tests First

- Construction and Session creation are CLI-safe.
- Lazy binding is single-instance under concurrent access.
- Request persists before the first response.
- Every `turn_end` persists once.
- Deltas never persist.
- Cancellation and early disposal do not lose turns already persisted at their authoritative boundary.
- Persistence is bounded even when a test provider ignores its cancellation token.
- Non-streaming usage is aggregated, not only the last message's usage.

### Verification

```bash
dotnet test src/sdks/pi-agent-sdk-csharp/tests/PiAgentSdk.MAF.Tests
```

---

## Task 8: Agw Integration, History Adapter, UI Bridge, and Isolation

### Goal

Register Pi as an external Agent while enforcing Agw-owned workspace, history, human interaction, Session binding, environment, and storage boundaries.

### Catalog and Factory

- Add `AgentNames.Pi` and a stable seeded ID after Codex.
- Classify known external Agent names once through the non-persisted, case-insensitive `ExternalAgentKind` resolver.
- Seed default Pi options with `PiProjectTrust.Deny` and no persisted Session values.
- Add the Pi arm to `TryCreateExternalAgent`.
- Add `CreatePiAgent` and a pure/testable options normalizer.
- Extend `UsesProviderSessionBinding` and `ResolveExternalProviderSession` with Codex-style semantics: no pre-generated ID, resume only when a stored ID exists.

### PiChatHistoryProvider

Create `Agw.Agents.ExternalAgents.Pi.PiChatHistoryProvider` as a `ChatHistoryProvider` wrapper around Agw's existing provider.

- Copy the protected override shape from the current compiled Microsoft.Agents.AI 1.15.0 API and the repository's `ClaudeCodeChatHistoryProvider`; do not rely on remembered method names.
- Forward `StateKeys` and invoking behavior to the inner provider.
- Sanitize request/authoritative response messages before forwarding invoked behavior.
- Remove transport-only raw representations and blank content.
- Mark System/User/Tool display records as excluded from model history and handoff.
- Preserve invocation exceptions and the execution/persistence failure precedence defined in Task 7.

### WrapPiAgent and Resource Ownership

The decoration step remains:

```csharp
internal AIAgent WrapPiAgent(AIAgent aiAgent, bool isBackground) =>
    DecorateExternalAgent(aiAgent, isBackground);
```

MAF middleware builders do not preserve `IAsyncDisposable`. `CreatePiAgent` must therefore retain the original `PiAgentAIAgent` and wrap the fully decorated result with `ResourceOwningAIAgent`. Disposing the final Agent must dispose that owner exactly once and terminate every live Pi RPC process.

The decorated chain adds observability, usage, and background approval/interaction rejection only. It must not include `ExternalAgentChatHistoryAgent`, `ClaudeCodeProviderSessionTrackingAgent`, or another Session-tracking wrapper. Pi's session callback travels through `PiAgentAIAgentOptions`, like Codex.

### PiExtensionUiBridge

Create a run-bound bridge similar to the Claude Code question bridge.

- Bind the current `IHumanInteractionChannel` around foreground runs.
- Map select, confirm, input, and editor payloads to `HumanInteractionRequest`.
- Verify matching response IDs and validate expected response data; a select value must exactly match one of `request.Options`.
- Honour request timeout.
- When background/no channel, return cancelled immediately.
- Fire-and-forget UI remains mapped as System status updates.
- Reject concurrent use of one bridge instance.

### Agw-Owned Directories

For the resolved stable execution user ID, construct:

```text
<user-home>/.pi/agent
<AgwDataPaths.Root>/external-agents/pi/<userId>/sessions
```

- Resolve the host Pi configuration from the OS user home and reuse its `auth.json`, `settings.json`, and `models.json` directly.
- Treat that configuration as process-level: Agw users under the same server OS account share provider identity and quota; only Pi Session files are user-scoped.
- Build the Session path from trusted server data plus the validated stable user ID, then canonicalize it.
- Verify the Session path remains below the expected Agw user root.
- Create only the Agw Session directory and restrict Unix permissions to the current OS user.
- Force `--no-extensions` so reusing host configuration does not execute global Pi extensions in the server process.
- Load only server-owned `ExternalAgents:Pi:Extensions` through explicit `--extension` arguments and force the server-owned `HistoryPersistenceTimeout`; Agent Extra cannot override either policy.
- Container/VM isolation remains a deployment requirement, not part of this feature.

### Reserved Settings and Environment

Agw owns these keys and removes caller/Extra values before setting them last:

```text
PI_CODING_AGENT_DIR
PI_CODING_AGENT_SESSION_DIR
PI_OFFLINE
PI_SKIP_VERSION_CHECK
PI_TELEMETRY
```

Normalize in this exact order:

1. Deserialize `ExtraSetting`.
2. Clear stale `SessionId`, `IsResume`, history provider, UI handler, and callback.
3. Remove reserved keys from global/session Extra environments and Agent/Project/turn environment input.
4. Force `WorkingDirectory` from `Project.Workspace`.
5. Force `SessionOptions.SessionDir` to the trusted canonical sessions path.
6. Merge allowed explicit runtime environment on top of the sanitized SDK base.
7. Set the host Pi config path, trusted Session path, and remaining PI variables last.
8. Apply the trusted extension allowlist and history timeout, then the current provider Session ID/resume flag, `PiChatHistoryProvider`, UI bridge handler, and session-start callback.

Agw must produce both channels with the same value:

```text
--session-dir <trusted-sessions-path>
PI_CODING_AGENT_SESSION_DIR=<trusted-sessions-path>
```

The CLI flag officially has higher precedence, but both channels are server-owned and identical. A stale or malicious `ExtraSetting.SessionDir` can never escape the trusted directory.

Set:

```text
PI_CODING_AGENT_DIR=<user-home>/.pi/agent
PI_CODING_AGENT_SESSION_DIR=<trusted-sessions-path>
PI_OFFLINE=1
PI_SKIP_VERSION_CHECK=1
PI_TELEMETRY=0
```

`PI_OFFLINE=1` is the primary startup-network restriction. `PI_SKIP_VERSION_CHECK=1` remains explicit for defense in depth and compatibility with future Pi behavior. Provider model calls continue through explicitly configured provider credentials.

Pi 0.84.4 emits UUIDv7 Session IDs. The provider binding persists the raw string, but the current shared Agw runtime converts it through `Guid`; if Pi adopts a non-Guid format, that plumbing must preserve the raw value instead of treating the binding as invalid.

Explicit Agw environment variables override ordinary `models.json` key expressions. Pi's fixed credential order remains `--api-key > auth.json > environment > models.json`; Agw does not pass plaintext credentials through `--api-key`, so an existing auth entry for the same provider retains priority.

### Agw Tests

- Catalog exposes seeded Pi options with deny trust.
- Workspace and environment are normalized.
- Extra Session ID/resume state is always cleared/replaced.
- Extra `SessionDir` cannot override the trusted CLI path.
- Extra/runtime `PI_CODING_AGENT_SESSION_DIR` cannot override the trusted env path.
- CLI SessionDir and env SessionDir are identical.
- Host Pi config reuse and Session path containment/user separation are enforced.
- Agw forces `--no-extensions` while reusing host configuration and passes only the server-owned explicit extension allowlist.
- Agw overrides an Extra history timeout with the positive server-owned `ExternalAgents:Pi:HistoryPersistenceTimeout` value.
- `PiChatHistoryProvider` sanitizes and persists request and authoritative turns only, including failed invocation context.
- `WrapPiAgent` has no external-history/tracking wrapper, and the final Agent remains `IAsyncDisposable` after all middleware composition.
- Foreground Extension UI reaches the current channel; background dialogs cancel.
- New and resumed provider-session bindings behave like Codex.
- Tests use fake `AIAgent`/pure normalizers and never construct a real CLI-backed Agent.

### Verification

```bash
dotnet test tests/Agw.Agents.Tests --filter "FullyQualifiedName~PiIntegrationAdapterTests|FullyQualifiedName~AgentRuntimeServiceCompositionTests|FullyQualifiedName~ExecutionRuntimeConfigurationTests"
dotnet test tests/Agw.Architecture.Tests
```

---

## Task 9: Documentation, Optional Compatibility Test, and Full Verification

### Goal

Document the supported behavior and prove the combined change is ready without silently depending on a developer's local Pi installation.

### README Requirements

Document:

- Installation of the pinned Pi baseline and `PiPathOverride`.
- RPC-only integration and `agent_settled` completion semantics.
- New and resumed persistent Session examples.
- `--session-dir` precedence over `PI_CODING_AGENT_SESSION_DIR`.
- Forced-kill recovery with a new `ResumeSession(id)` object.
- `NoSession` nonrecoverability.
- Configurable command and abort timeouts.
- Sanitized environment, proxy/certificate pass-through, and noninheritance of arbitrary secrets.
- Process-level sharing of the host Pi provider identity/quota versus per-user Pi Session storage.
- Project trust default and lack of an in-process sandbox.
- Server-owned explicit extension allowlisting and the history-persistence timeout.
- Foreground Extension UI and background cancellation behavior.
- Pi/MAF Tool boundary: Pi executes its own Tools; Tool calls are informational to MAF.
- `BashExecutionMessage` belongs to Pi's direct RPC `bash` command, is not part of `turn_end.toolResults`, and remains outside v1.
- Protocol upgrade procedure.

### Optional Real CLI Suite

- Gate with an explicit environment/configuration switch.
- Skip cleanly when the executable is absent.
- Verify the executable version matches the recorded compatibility baseline.
- Cover startup/get_state, one prompt through `agent_settled`, abort/drain, and persistent resume.
- Use an isolated temporary config/session directory and no production credentials.

### Full Verification

```bash
dotnet build Agw.slnx
dotnet test Agw.slnx
dotnet csharpier check src/sdks/pi-agent-sdk-csharp src/server/Agw.Agents tests/Agw.Agents.Tests
git diff --check
git status --short
```

### Final Acceptance Checklist

- [ ] Version baseline was confirmed from npm/package metadata.
- [ ] Default/unit tests do not start the real Pi CLI.
- [ ] All RPC pending requests have explicit terminal paths.
- [ ] Cancellation drains or kills before releasing the run gate.
- [ ] Forced-kill recovery works through a new `ResumeSession(id)`.
- [ ] Unknown protocol variants are preserved rather than silently dropped.
- [ ] Tool calls are not duplicated and Tool argument types are intact.
- [ ] Handoff context and image-only input reach Pi.
- [ ] Foreground dialogs from server-allowlisted extensions bridge; background dialogs cancel.
- [ ] History contains requests and authoritative turns, not deltas.
- [ ] The final composed Pi Agent is disposable and releases the original `PiAgentAIAgent` exactly once.
- [ ] Agw overrides both SessionDir channels with the same trusted path.
- [ ] Reserved PI keys cannot be overridden through Extra or runtime environment.
- [ ] Proxy and certificate variables survive the sanitized environment.
- [ ] `LC_` is implemented as a prefix rule, not a literal key.
- [ ] No raw prompts, reasoning, Tool output, API keys, or full stderr are logged.
- [ ] No migrations were added.
- [ ] No unrelated files were changed.
- [ ] Nothing was staged or committed without explicit authorization.
