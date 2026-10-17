# Project Learning Log

## Chat scroll measurement

Virtualized row measurements can reduce `scrollTop` while the viewport remains at the bottom. Preserve follow-bottom within the bottom tolerance; only upward motion outside that tolerance pauses it. See [auto-scroll](../src/clients/packages/chat-core/src/auto-scroll.ts) and its adjacent tests.

## External Agent request persistence

Stage the original input before forwarding transient memory-enriched messages. [HistoryRecordingAgent](../src/server/Agw.Agents.Execution/Agents/History/HistoryRecordingAgent.cs) stages input and excludes forwarded copies from persistence; [AgwChatHistoryProvider](../src/server/Agw.Agents.Execution/Agents/History/AgwChatHistoryProvider.cs) manages each SDK session's recording. Projects' [ConversationHistoryStore](../src/server/Agw.Projects/Infrastructure/ConversationHistoryStore.cs) owns history persistence and buffering.

## Workflow checkpoint restore

Send the startup `TurnToken` only for a fresh run. A restored MAF checkpoint already contains pending work; another token can replay entry nodes. Verify Agent invocation counts and checkpoint occurrences, not only output counts. The shared rule is documented in the [Execution README](../src/server/Agw.Agents.Execution/README.md#agentflow-runtime-协作边界).
