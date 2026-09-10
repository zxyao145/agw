# Project Learning Log

## Chat scroll measurement

Virtualized row measurements can reduce `scrollTop` while the viewport remains at the bottom. Preserve follow-bottom within the bottom tolerance; only upward motion outside that tolerance pauses it. See [auto-scroll](../src/clients/packages/chat-core/src/auto-scroll.ts) and its adjacent tests.

## External Agent request persistence

Stage the original input before forwarding transient memory-enriched messages. Request lifecycle belongs to Projects' [EfCoreChatHistoryProvider](../src/server/Agw.Projects/Infrastructure/EfCoreChatHistoryProvider.Requests.cs), exposed through `IConversationHistoryRequests`; [AgentRequestContextAgent](../src/server/Agw.Agents.Execution/Agents/Context/AgentRequestContextAgent.cs) must not persist injected context as new user history. Do not restore the removed duplicate request-history provider.

## Workflow checkpoint restore

Send the startup `TurnToken` only for a fresh run. A restored MAF checkpoint already contains pending work; another token can replay entry nodes. Verify Agent invocation counts and checkpoint occurrences, not only output counts. The shared rule is documented in the [Execution README](../src/server/Agw.Agents.Execution/README.md#agentflow-runtime-协作边界).
