import assert from "node:assert/strict";
import { readFile } from "node:fs/promises";
import test from "node:test";

const CHAT_URL = new URL("./chat.tsx", import.meta.url);
const CHAT_INPUT_URL = new URL("./chat-input.tsx", import.meta.url);
const CHECKPOINT_CARD_URL = new URL("./agentflow-checkpoint-card.tsx", import.meta.url);
const PACKAGES_URL = new URL("../../../../../", import.meta.url);
const SEARCH_FILE_URL = new URL("../../../lib/chat/search-file.ts", import.meta.url);
const EXECUTION_HUB_URL = new URL("../../../services/execution-hub.ts", import.meta.url);
const EXECUTION_SESSION_MANAGER_URL = new URL(
  "../../../services/execution-session-manager.ts",
  import.meta.url,
);
const EXECUTION_RECONNECTING_DIALOG_URL = new URL(
  "./execution-reconnecting-dialog.tsx",
  import.meta.url,
);
const CHAT_WORKSPACE_URL = new URL("../../pages/chat/chat-workspace.tsx", import.meta.url);
const AGENT_DRAWER_URL = new URL(
  "agents/src/ui-web/pages/agents/components/execute-agent-drawer.tsx",
  PACKAGES_URL,
);
const AGENTFLOW_DRAWER_URL = new URL(
  "agents/src/ui-web/pages/agentflows/components/execute-agentflow-drawer.tsx",
  PACKAGES_URL,
);

test("chat page and execution drawers render the shared Chat container", async () => {
  const [pageSource, agentDrawerSource, agentflowDrawerSource] = await Promise.all([
    readFile(CHAT_WORKSPACE_URL, "utf8"),
    readFile(AGENT_DRAWER_URL, "utf8"),
    readFile(AGENTFLOW_DRAWER_URL, "utf8"),
  ]);

  assert.match(
    pageSource,
    /import \{ Chat, type ChatSessionSeed \} from "\.\.\/\.\.\/components\/message\/chat"/,
  );
  assert.match(pageSource, /<Chat[\s\S]*?target=\{selectedTarget\}[\s\S]*?sessionSeed=/);
  assert.doesNotMatch(pageSource, /<Conversation\b|<InputArea\b|new ExecutionHubClient/);

  assert.match(
    agentDrawerSource,
    /<Chat[\s\S]*?target=\{\{ id: executingAgent\.id, type: "agent" \}\}/,
  );
  assert.match(agentDrawerSource, /sessionSeed=/);
  assert.doesNotMatch(agentDrawerSource, /targetId=|agentType=/);

  assert.match(
    agentflowDrawerSource,
    /<Chat[\s\S]*?target=\{\{ id: agentflow\.id, type: "agentflow" \}\}/,
  );
  assert.match(agentflowDrawerSource, /sessionSeed=/);
  assert.doesNotMatch(agentflowDrawerSource, /targetId=|agentType=/);
});

test("chat page keeps the live Chat mounted while the files tab is active", async () => {
  const pageSource = await readFile(CHAT_WORKSPACE_URL, "utf8");

  assert.match(
    pageSource,
    /<TabsContent[\s\S]*?value="chat"[\s\S]*?forceMount[\s\S]*?data-\[state=inactive\]:hidden/,
  );
});

test("shared Chat owns canonical message filtering, grouping, usage, and managed execution state", async () => {
  const [source, managerSource] = await Promise.all([
    readFile(CHAT_URL, "utf8"),
    readFile(EXECUTION_SESSION_MANAGER_URL, "utf8"),
  ]);

  assert.match(source, /export interface ChatSessionSeed/);
  assert.match(source, /target: Pick<ChatTargetOption, "id" \| "type"> \| null/);
  assert.match(source, /sessionSeed: ChatSessionSeed/);
  assert.match(source, /executionSessionManager\.attach/);
  assert.match(source, /executionClientRef\.current\?\.detach\(\)/);
  assert.match(source, /\[detachExecution, sessionSeed\.revision\]/);
  assert.match(source, /getClaudeInitCommands\(message\)/);
  assert.match(source, /getMessageTokenUsage\(message\)/);
  assert.match(source, /stripUsageContents\(messages\)/);
  assert.match(source, /<Conversation[\s\S]*?scrollable=\{false\}/);
  assert.doesNotMatch(source, /processMessages=/);
  assert.match(source, /<ChatInput/);
  assert.doesNotMatch(source, /new ExecutionHubClient/);
  assert.match(managerSource, /new ExecutionHubClient/);
  assert.match(managerSource, /private readonly entries = new Map/);
});

test("shared Chat reattaches managed execution after history hydration", async () => {
  const source = await readFile(CHAT_URL, "utf8");
  const attachmentStart = source.indexOf("if (!executionSessionManager.has(key))");
  const attachmentEnd = source.indexOf("const ensureConfiguredClient", attachmentStart);

  assert.notEqual(attachmentStart, -1);
  assert.notEqual(attachmentEnd, -1);
  assert.match(source.slice(attachmentStart, attachmentEnd), /sessionSeed\.revision/);
});

test("shared Chat defaults agent mode to Execute without a persisted mode snapshot", async () => {
  const source = await readFile(CHAT_URL, "utf8");

  assert.match(source, /const DEFAULT_AGENT_MODE: AgentMode = "execute"/);
  assert.match(source, /return DEFAULT_AGENT_MODE;/);
  assert.match(source, /confirmedAgentModeRef\.current = DEFAULT_AGENT_MODE/);
  assert.match(source, /setAgentMode\(DEFAULT_AGENT_MODE\)/);
});

test("shared Chat keeps unmatched human interactions in the scrollable conversation footer", async () => {
  const source = await readFile(CHAT_URL, "utf8");

  assert.match(
    source,
    /hasMatchingHumanInteractionCall\(visibleMessages, pendingHumanInteraction\)/,
  );
  assert.match(source, /pendingHumanInteraction=\{pendingHumanInteraction\}/);
  assert.match(
    source,
    /<Conversation[\s\S]*?footer=\{[\s\S]*?request=\{floatingHumanGate\}[\s\S]*?\/>/,
  );
  assert.doesNotMatch(
    source,
    /bottom-\[calc\(100%\+0\.5rem\)\][\s\S]*?request=\{floatingHumanGate\}/,
  );
});

test("shared Chat scopes history and batches incoming streaming messages", async () => {
  const source = await readFile(CHAT_URL, "utf8");

  assert.match(source, /scopeMessagesByUserTurn\(preparedHistory\.messages\)/);
  assert.match(source, /const activeStreamingScopeRef = React\.useRef<string \| null>\(null\)/);
  assert.match(
    source,
    /activeStreamingScopeRef\.current \?\?=[\s\S]*?getMessageStreamingScopeId\(message\) \?\? message\.messageId;[\s\S]*?setIsExecuting\(true\)/,
  );
  assert.match(source, /activeStreamingScopeRef\.current = userMessage\.messageId/);
  assert.match(
    source,
    /scopeStreamingMessage\([\s\S]*?message,[\s\S]*?activeStreamingScopeRef\.current/,
  );
  assert.match(
    source,
    /createStreamingMessageBatcher\([\s\S]*?mergeStreamingMessages\(current, incomingMessages\)/,
  );
  assert.match(
    source,
    /streamingMessageBatcherRef\.current\?\.enqueue\(scopedMessage, generation\)/,
  );
  assert.match(source, /streamingMessageBatcherRef\.current\?\.flush\(generation\)/);
  assert.doesNotMatch(source, /mergeStreamingMessagesById/);
});

test("Agentflow checkpoint resume uses an exact occurrence and truncates only after success", async () => {
  const [source, inputSource, cardSource] = await Promise.all([
    readFile(CHAT_URL, "utf8"),
    readFile(CHAT_INPUT_URL, "utf8"),
    readFile(CHECKPOINT_CARD_URL, "utf8"),
  ]);

  assert.match(source, /target\?\.type === "agentflow"/);
  assert.match(source, /checkpoint\.boundarySequence > latest\.boundarySequence/);
  assert.match(
    source,
    /checkpointResumeDisabled =\s*isExecuting \|\| isTransitioning \|\| reconnectState !== null/,
  );
  assert.match(source, /checkpoint\.occurrenceId === occurrenceId && checkpoint\.available/);
  assert.match(source, /message\.streamingScopeId === resumedStreamingScopeId/);
  assert.ok(
    source.indexOf("await client.resumeCheckpoint") < source.lastIndexOf("truncateAtCheckpoint("),
  );
  assert.match(source, /setConversationUsage\(calculateConversationUsage\(retainedMessages\)\)/);

  const resumeIndex = inputSource.indexOf("Resume");
  const quickTextIndex = inputSource.indexOf("<QuickTextDialog");
  assert.ok(resumeIndex >= 0 && resumeIndex < quickTextIndex);
  assert.match(cardSource, /The workflow continued automatically\./);
  assert.match(cardSource, /disabled=\{disabled \|\| !available\}/);
});

test("shared Chat restores only a hydrated durable attachment", async () => {
  const source = await readFile(CHAT_URL, "utf8");
  const pageSource = await readFile(CHAT_WORKSPACE_URL, "utf8");

  assert.match(source, /restoreDurableExecution\?: boolean/);
  assert.match(source, /hydratedSessionRevision !== sessionSeed\.revision/);
  assert.match(source, /!hasPersistedDurableExecution\(\{ projectId, contextId \}\)/);
  assert.match(source, /ensureConfiguredClient\(contextId, generation\)/);
  assert.match(
    source,
    /onReconnected:[\s\S]*?setIsExecuting\([\s\S]*?attachedClient\.getStatus\(\)/,
  );
  assert.match(
    pageSource,
    /restoreDurableExecution=\{[\s\S]*?Number\(chatSessionSeed\.revision\) > 0[\s\S]*?queryProjectId === selectedProjectId[\s\S]*?queryContextId === contextId[\s\S]*?chatSessionSeed\.contextId === contextId/,
  );
});

test("shared Chat follows streaming output only while the viewport is at the bottom", async () => {
  const source = await readFile(CHAT_URL, "utf8");

  assert.match(source, /import \{ updateAutoScrollState, type AutoScrollState \}/);
  assert.match(
    source,
    /const autoScrollStateRef = React\.useRef<AutoScrollState>\(\{[\s\S]*?shouldAutoScroll: true,[\s\S]*?scrollHeight: 0,[\s\S]*?scrollTop: 0/,
  );
  assert.match(
    source,
    /autoScrollStateRef\.current = updateAutoScrollState\([\s\S]*?autoScrollStateRef\.current,[\s\S]*?event\.currentTarget/,
  );
  assert.match(source, /ref=\{conversationScrollRef\}/);
  assert.match(
    source,
    /if \(autoScrollStateRef\.current\.shouldAutoScroll\) \{[\s\S]*?scrollContainer\.scrollTop = scrollContainer\.scrollHeight/,
  );
  assert.match(
    source,
    /scrollHeight: scrollContainer\.scrollHeight,[\s\S]*?scrollTop: scrollContainer\.scrollTop/,
  );
  assert.match(
    source,
    /autoScrollStateRef\.current = \{[\s\S]*?shouldAutoScroll: true,[\s\S]*?scrollHeight: 0,[\s\S]*?scrollTop: 0,[\s\S]*?\};[\s\S]*?setMessages\(preparedHistory\.messages\)/,
  );
  assert.match(source, /onScroll=\{handleConversationScroll\}/);
});

test("shared Chat rejects stale async continuations and reports operation errors once", async () => {
  const [source, executionHubSource] = await Promise.all([
    readFile(CHAT_URL, "utf8"),
    readFile(EXECUTION_HUB_URL, "utf8"),
  ]);

  assert.doesNotMatch(source, /onError: \(error: Error\)/);
  assert.match(
    source,
    /await client\.configure\([\s\S]*?if \([\s\S]*?generation !== executionGenerationRef\.current[\s\S]*?executionClientRef\.current !== client[\s\S]*?\) \{[\s\S]*?return;/,
  );
  assert.match(
    source,
    /await client\.execute\([\s\S]*?if \([\s\S]*?generation !== executionGenerationRef\.current[\s\S]*?executionClientRef\.current !== client[\s\S]*?\) \{[\s\S]*?return;/,
  );
  assert.match(source, /const reportExecutionErrorOnce = \(error: unknown\) =>/);
  assert.match(
    source,
    /onClose:[\s\S]*?configuredSessionRef\.current = null[\s\S]*?executionClientRef\.current = null/,
  );
  assert.match(source, /setPendingHumanGate\(\(current\) =>/);
  assert.match(source, /current\?\.requestId === requestId \? null : current/);
  assert.match(source, /client\s*\.submitHumanResponse\([\s\S]*?\.catch\(/);
  assert.match(source, /const \[isTransitioning, setIsTransitioning\] = React\.useState\(false\)/);
  assert.match(source, /await client\.interruptAndWait\(reason\)/);
  assert.match(source, /<ChatInput[\s\S]*?isTransitioning=\{isTransitioning\}/);
  assert.match(executionHubSource, /public async interruptAndWait\(reason\?: string\)/);
  assert.match(
    executionHubSource,
    /HubConnectionState\.Reconnecting[\s\S]*?await this\.waitForReconnect\(\)/,
  );
});

test("shared Chat blocks the full chat workspace while SignalR reconnects", async () => {
  const [source, dialogSource, managerSource, workspaceSource] = await Promise.all([
    readFile(CHAT_URL, "utf8"),
    readFile(EXECUTION_RECONNECTING_DIALOG_URL, "utf8"),
    readFile(EXECUTION_SESSION_MANAGER_URL, "utf8"),
    readFile(CHAT_WORKSPACE_URL, "utf8"),
  ]);

  assert.match(source, /onReconnecting: \(state\) =>/);
  assert.match(source, /onReconnectFailed: \(state\) =>/);
  assert.match(source, /onReconnected: \(\) =>/);
  assert.match(source, /inert=\{reconnectState !== null\}/);
  assert.doesNotMatch(source, /ExecutionReconnectingDialog/);
  assert.match(managerSource, /getReconnectState: \(\) => attachedEntry\.reconnectState/);
  assert.match(managerSource, /public async retryConnection\(key: ExecutionSessionKey\)/);
  assert.match(dialogSource, /role="dialog"/);
  assert.match(dialogSource, /items-start justify-center/);
  assert.match(dialogSource, /pt-\[150px\]/);
  assert.match(dialogSource, /Reconnecting to Server…/);
  assert.match(dialogSource, /state\.status === "failed"/);
  assert.match(dialogSource, /Failed to rejoin\. Please retry or reload the page\./);
  assert.match(dialogSource, /!isFailed && "animate-spin/);
  assert.match(dialogSource, /onClick=\{onRetry\}/);
  assert.match(dialogSource, />\s*Retry\s*</);
  assert.match(dialogSource, /You can still switch Server or open Settings/);
  assert.match(workspaceSource, /inert=\{executionReconnectState !== null\}/);
  assert.match(workspaceSource, /onReconnectStateChange=\{setExecutionReconnectState\}/);
  assert.match(workspaceSource, /onRetry=\{handleReconnectRetry\}/);
  assert.match(
    workspaceSource,
    /\{executionReconnectState \? \([\s\S]*?<ExecutionReconnectingDialog[\s\S]*?state=\{executionReconnectState\}/,
  );
});

test("shared Chat input provides slash and project file suggestions", async () => {
  const [chatSource, inputSource, searchFileSource] = await Promise.all([
    readFile(CHAT_URL, "utf8"),
    readFile(CHAT_INPUT_URL, "utf8"),
    readFile(SEARCH_FILE_URL, "utf8"),
  ]);

  assert.match(chatSource, /getAgentSuggestionQueryParams\(projectId, target\)/);
  assert.match(chatSource, /toCommandSource\(agentSuggestionsQuery\.data, claudeCommands\)/);
  assert.match(inputSource, /resolveInputSuggestions\(input, caretIndex, commandSource/);
  assert.match(inputSource, /searchFile\(projectId, keyword\)/);
  assert.match(searchFileSource, /toFileSuggestions\(response\.results\)/);
});

test("shared Chat renders only the pending file comment count in the composer", async () => {
  const [chatSource, inputSource, workspaceSource] = await Promise.all([
    readFile(CHAT_URL, "utf8"),
    readFile(CHAT_INPUT_URL, "utf8"),
    readFile(CHAT_WORKSPACE_URL, "utf8"),
  ]);

  assert.match(chatSource, /pendingFileCommentCount=\{pendingFileComments\.length\}/);
  assert.match(inputSource, /pendingFileCommentCount: number/);
  assert.match(inputSource, /\{pendingFileCommentCount\} code comment/);
  assert.match(inputSource, /aria-label="Clear pending code comments"/);
  assert.match(
    inputSource,
    /hasAdditionalInput=\{pendingFileCommentCount > 0 \|\| imageAttachments\.length > 0\}/,
  );
  assert.doesNotMatch(inputSource, /LineComment|filePath|lineNumber|comment\.content/);
  assert.match(workspaceSource, /pendingFileComments=\{comments\}/);
  assert.match(workspaceSource, /new Set\(commentIds\)/);
});

test("shared Chat input intercepts image clipboard files and renders removable previews", async () => {
  const [chatSource, inputSource] = await Promise.all([
    readFile(CHAT_URL, "utf8"),
    readFile(CHAT_INPUT_URL, "utf8"),
  ]);

  assert.match(inputSource, /event\.clipboardData\.items/);
  assert.match(inputSource, /event\.preventDefault\(\)/);
  assert.match(inputSource, /createImageAttachments\(imageFiles\)/);
  assert.match(inputSource, /isSubmitDisabled=\{isReadingImages\}/);
  assert.doesNotMatch(inputSource, /isDisabled=\{isReadingImages\}/);
  assert.match(inputSource, /aria-label="Pasted images"/);
  assert.match(inputSource, /handleRemoveImage\(attachment\.id\)/);
  assert.match(chatSource, /createUserMessage\(resolvedInput, imageAttachments\)/);
});

test("shared Chat consumes only submitted file comments after execution succeeds", async () => {
  const source = await readFile(CHAT_URL, "utf8");
  const executeIndex = source.indexOf("await client.execute");
  const removeIndex = source.indexOf("onPendingFileCommentsRemove?.(", executeIndex);

  assert.match(source, /const submittedFileComments = \[\.\.\.pendingFileComments\]/);
  assert.match(source, /buildFileCommentPrompt\(value, submittedFileComments\)/);
  assert.ok(executeIndex >= 0 && removeIndex > executeIndex);
  assert.match(
    source.slice(executeIndex, removeIndex),
    /generation !== executionGenerationRef\.current[\s\S]*?executionClientRef\.current !== client/,
  );
  assert.match(source, /submittedFileComments\.map\(\(comment\) => comment\.id\)/);
});
