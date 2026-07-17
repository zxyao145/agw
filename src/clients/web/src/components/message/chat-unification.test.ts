import assert from "node:assert/strict";
import { readFile } from "node:fs/promises";
import test from "node:test";

const CHAT_URL = new URL("./chat.tsx", import.meta.url);
const CHAT_INPUT_URL = new URL("./chat-input.tsx", import.meta.url);
const SEARCH_FILE_URL = new URL("../../lib/chat/search-file.ts", import.meta.url);
const EXECUTION_HUB_URL = new URL("../../api/execution-hub.ts", import.meta.url);
const EXECUTION_SESSION_MANAGER_URL = new URL(
  "../../lib/execution-session-manager.ts",
  import.meta.url,
);
const CHAT_WORKSPACE_URL = new URL(
  "../../app/(app)/(interface)/chat/chat-workspace.tsx",
  import.meta.url,
);
const AGENT_DRAWER_URL = new URL(
  "../../app/(app)/(agents)/agents/components/execute-agent-drawer.tsx",
  import.meta.url,
);
const AGENTFLOW_DRAWER_URL = new URL(
  "../../app/(app)/(agents)/agentflows/components/execute-agentflow-drawer.tsx",
  import.meta.url,
);

test("chat page and execution drawers render the shared Chat container", async () => {
  const [pageSource, agentDrawerSource, agentflowDrawerSource] = await Promise.all([
    readFile(CHAT_WORKSPACE_URL, "utf8"),
    readFile(AGENT_DRAWER_URL, "utf8"),
    readFile(AGENTFLOW_DRAWER_URL, "utf8"),
  ]);

  assert.match(pageSource, /import \{ Chat \} from "@\/components\/message\/chat"/);
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

test("shared Chat scopes history and merges only the incoming streaming message", async () => {
  const source = await readFile(CHAT_URL, "utf8");

  assert.match(source, /scopeMessagesByUserTurn\(preparedHistory\.messages\)/);
  assert.match(source, /const activeStreamingScopeRef = React\.useRef<string \| null>\(null\)/);
  assert.match(
    source,
    /activeStreamingScopeRef\.current \?\?= message\.messageId;[\s\S]*?setIsExecuting\(true\)/,
  );
  assert.match(source, /activeStreamingScopeRef\.current = userMessage\.messageId/);
  assert.match(
    source,
    /scopeStreamingMessage\([\s\S]*?message,[\s\S]*?activeStreamingScopeRef\.current/,
  );
  assert.match(
    source,
    /setMessages\(\(current\) => mergeStreamingMessage\(current, scopedMessage\)\)/,
  );
  assert.doesNotMatch(source, /mergeStreamingMessagesById/);
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
});

test("shared Chat input provides slash and project file suggestions", async () => {
  const [chatSource, inputSource, searchFileSource] = await Promise.all([
    readFile(CHAT_URL, "utf8"),
    readFile(CHAT_INPUT_URL, "utf8"),
    readFile(SEARCH_FILE_URL, "utf8"),
  ]);

  assert.match(chatSource, /getAgentSuggestionQueryParams\(projectId, target\)/);
  assert.match(chatSource, /toCommandSource\(agentSuggestionsQuery\.data, claudeCommands\)/);
  assert.match(inputSource, /getTrailingSuggestionTrigger\(input\)/);
  assert.match(inputSource, /searchCommand\(trigger\.query, commandSource\)/);
  assert.match(inputSource, /searchFile\(projectId, trigger\.query\)/);
  assert.match(searchFileSource, /response\.results\.slice\(0, 5\)/);
});
