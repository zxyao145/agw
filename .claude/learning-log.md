# Project Learning Log

## 2026-08-25 | Bug Root Cause | Initial Chat Measurement Can Mimic an Upward Scroll

The first virtualized chat rows can shrink after their estimated heights are measured, clamping
`scrollTop` upward while the viewport is still at the bottom. Treating every decrease in
`scrollTop` as user intent disables follow-bottom during the first streamed conversation.
**Files:** `src/clients/packages/chat-core/src/auto-scroll.ts`,
`src/clients/packages/chat-core/src/auto-scroll.test.ts`
**Resolution:** Preserve auto-scroll whenever the current metrics are within the bottom tolerance;
only interpret upward movement outside that tolerance as a pause request.

## 2026-08-31 | Bug Root Cause | External Agent Memory Needs SDK-Specific Prompt Shaping

Claude Code MAF consumes only the first User message, while Codex and Pi consume multiple User messages.
Also, `AIContextProvider` source attribution alone does not prevent External Agent history adapters from
persisting injected context.
**Files:** `src/server/Agw.Agents/Execution/Agents/AgentRequestContextAgent.cs`,
`src/server/Agw.Agents/Execution/Agents/AgentRequestChatHistoryProvider.cs`
**Resolution:** Stage the original request once for every Agent type, forward one transient composite request to
the model, and let response-only history adapters consume the staged request independently of SDK request callbacks.


## 2026-09-05 | Bug Root Cause | A Second TurnToken Restarts a Restored Workflow
MAF 1.15.0 restores pending messages and requests from checkpoints; sending a new TurnToken after ResumeStreamingAsync restarts the entry executor and can duplicate Agent calls and output. The old path called the restored Agent twice; restricting the token to fresh runs passed 800 parallel restore cases.
**Files:** `src/server/Agw.Agents/Execution/Agentflows/DurableAgentflowSegmentRunner.cs`, `src/server/Agw.Agents/Execution/Agentflows/InProcessAgentflowRunner.cs`
**Resolution:** Send the startup token only for a fresh run. Assert restored Agent invocation counts and that upstream nodes/checkpoint occurrences are not replayed; do not infer correctness only from a sometimes-stable output count.
