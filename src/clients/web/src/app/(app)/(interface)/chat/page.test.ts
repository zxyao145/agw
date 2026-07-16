import assert from "node:assert/strict";
import { readFile } from "node:fs/promises";
import test from "node:test";

const CHAT_PAGE_URL = new URL("./page.tsx", import.meta.url);
const CHAT_COMPONENT_URL = new URL("../../../../components/message/chat.tsx", import.meta.url);
const CONVERSATION_DETAILS_PAGE_URL = new URL(
  "../../(tasks)/projects/conversations/details/page.tsx",
  import.meta.url,
);
const JOB_LOGS_PAGE_URL = new URL("../../(jobs)/jobs/logs/page.tsx", import.meta.url);
const CONVERSATION_LIST_URL = new URL(
  "../../../../components/task/conversation-list.tsx",
  import.meta.url,
);
const TASK_CLIENT_URL = new URL("../../../../api/task-client.ts", import.meta.url);

test("chat page refreshes the conversation list after an execution completes", async () => {
  const [pageSource, conversationListSource] = await Promise.all([
    readFile(CHAT_PAGE_URL, "utf8"),
    readFile(CONVERSATION_LIST_URL, "utf8"),
  ]);

  assert.match(conversationListSource, /refreshSignal\?: number;/);
  assert.match(conversationListSource, /\[refreshSignal, refreshContexts\]/);
  assert.match(
    pageSource,
    /const \[conversationListRefreshSignal, setConversationListRefreshSignal\] = React\.useState\(0\)/,
  );
  assert.match(pageSource, /setConversationListRefreshSignal\(\(signal\) => signal \+ 1\)/);
  assert.match(pageSource, /refreshSignal=\{conversationListRefreshSignal\}/);
  assert.match(pageSource, /onConversationChange=\{refreshConversationList\}/);
});

test("shared chat preserves streamed messages after an execution completes", async () => {
  const chatSource = await readFile(CHAT_COMPONENT_URL, "utf8");
  const terminalBranchStart = chatSource.indexOf("const terminalStatus =");
  const nextMessageBranchStart = chatSource.indexOf(
    'if (message.role !== "user")',
    terminalBranchStart,
  );

  assert.notEqual(terminalBranchStart, -1);
  assert.notEqual(nextMessageBranchStart, -1);

  const terminalBranch = chatSource.slice(terminalBranchStart, nextMessageBranchStart);
  assert.doesNotMatch(terminalBranch, /getProjectContextDetails|setMessages/);
});

test("conversation list ignores stale refresh responses", async () => {
  const conversationListSource = await readFile(CONVERSATION_LIST_URL, "utf8");

  assert.match(conversationListSource, /refreshRequestIdRef/);
  assert.match(conversationListSource, /requestId !== refreshRequestIdRef\.current/);
});

test("chat contexts use the shared friendly local date-time formatter", async () => {
  const conversationListSource = await readFile(CONVERSATION_LIST_URL, "utf8");

  assert.match(conversationListSource, /formatFriendlyLocalDateTime/);
  assert.match(
    conversationListSource,
    /formatFriendlyLocalDateTime\(context\.updateTime \?\? context\.createTime\)/,
  );
  assert.doesNotMatch(conversationListSource, /const formatDate =/);
});

test("chat context list keeps cleared contexts and filters empty execution placeholders", async () => {
  const taskClientSource = await readFile(TASK_CLIENT_URL, "utf8");

  assert.match(taskClientSource, /function shouldIncludeContext/);
  assert.match(taskClientSource, /context\.messageCount > 0 \|\| context\.executionCount === 0/);
  assert.match(taskClientSource, /\.filter\(shouldIncludeContext\)/);
});

test("chat page resolves the active context from context id only", async () => {
  const [pageSource, conversationListSource] = await Promise.all([
    readFile(CHAT_PAGE_URL, "utf8"),
    readFile(CONVERSATION_LIST_URL, "utf8"),
  ]);

  assert.doesNotMatch(conversationListSource, new RegExp("current" + "Task" + "Id"));
  assert.doesNotMatch(conversationListSource, new RegExp("latest" + "Task" + "Id"));
  assert.match(conversationListSource, /context\.contextId === currentContextId/);
  assert.match(conversationListSource, /onActiveContextResolved/);
  assert.match(pageSource, /currentContextId=\{contextId\}/);
  assert.match(pageSource, /setContextId\(context\.contextId\)/);
  assert.match(pageSource, /syncRoute\(selectedProjectId, context\.contextId\)/);
});

test("chat routes keep project and context parameters without URL settings", async () => {
  const pageSource = await readFile(CHAT_PAGE_URL, "utf8");

  assert.match(pageSource, /nextParams\.set\("projectId", projectId\)/);
  assert.match(pageSource, /nextParams\.set\("contextId", contextId\)/);
  assert.doesNotMatch(
    pageSource,
    /url-settings|hashSettingsValue|routeSettingsParam|settingsHash|hashchange/,
  );
});

test("chat page clears legacy settings URLs without restoring their values", async () => {
  const pageSource = await readFile(CHAT_PAGE_URL, "utf8");

  assert.match(pageSource, /searchParams\.delete\("settings"\)/);
  assert.match(pageSource, /window\.history\.replaceState/);
  assert.doesNotMatch(pageSource, /decodeChatUrlSettings|getChatSettingsHashValue/);
});

test("chat page does not expose the share current URL action", async () => {
  const pageSource = await readFile(CHAT_PAGE_URL, "utf8");

  assert.doesNotMatch(
    pageSource,
    /Share2|copyCurrentUrlToClipboard|handleShareCurrentUrl|Share current URL/,
  );
});

test("chat routes do not read or generate execution id query parameters", async () => {
  const [pageSource, conversationDetailsSource, jobLogsSource] = await Promise.all([
    readFile(CHAT_PAGE_URL, "utf8"),
    readFile(CONVERSATION_DETAILS_PAGE_URL, "utf8"),
    readFile(JOB_LOGS_PAGE_URL, "utf8"),
  ]);
  const forbidden = "task" + "Id";

  assert.doesNotMatch(pageSource, new RegExp(`searchParams\\.get\\("${forbidden}"\\)`));
  assert.doesNotMatch(pageSource, new RegExp(`nextParams\\.set\\("${forbidden}"`));
  assert.doesNotMatch(pageSource, new RegExp(`searchParams\\.delete\\("${forbidden}"\\)`));
  assert.doesNotMatch(conversationDetailsSource, new RegExp(`searchParams\\.set\\("${forbidden}"`));
  assert.doesNotMatch(jobLogsSource, new RegExp(`/chat\\?[^"]*${forbidden}`));
});
