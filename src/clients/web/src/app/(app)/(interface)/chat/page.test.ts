import assert from "node:assert/strict";
import { readFile } from "node:fs/promises";
import test from "node:test";

const WEB_CHAT_PAGE_URL = new URL("./page.tsx", import.meta.url);
const CHAT_WORKSPACE_URL = new URL("./chat-workspace.tsx", import.meta.url);
const DESKTOP_CHAT_PAGE_URL = new URL("../../desktop/chat/page.tsx", import.meta.url);
const CHAT_ROUTE_BOUNDARY_URL = new URL(
  "../../../../components/chat/chat-route-boundary.tsx",
  import.meta.url,
);
const CHAT_ROUTE_URL = new URL("../../../../lib/chat-route.ts", import.meta.url);
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

test("Web and Desktop routes compose one shared Chat workspace", async () => {
  const [webPageSource, desktopPageSource, workspaceSource, boundarySource] = await Promise.all([
    readFile(WEB_CHAT_PAGE_URL, "utf8"),
    readFile(DESKTOP_CHAT_PAGE_URL, "utf8"),
    readFile(CHAT_WORKSPACE_URL, "utf8"),
    readFile(CHAT_ROUTE_BOUNDARY_URL, "utf8"),
  ]);

  assert.match(webPageSource, /<ChatWorkspace routeBasePath="\/chat" showProjectSelect\s*\/>/);
  assert.doesNotMatch(webPageSource, /compactToolbar/);
  assert.match(
    desktopPageSource,
    /<ChatWorkspace[\s\S]*routeBasePath="\/desktop\/chat"[\s\S]*showProjectSelect=\{false\}[\s\S]*compactToolbar[\s\S]*\/>/,
  );
  assert.match(workspaceSource, /export function ChatWorkspace\(/);
  assert.match(workspaceSource, /compactToolbar\?: boolean/);
  assert.match(workspaceSource, /size=\{compactToolbar \? "sm" : "default"\}/);
  assert.match(workspaceSource, /compactToolbar && "h-8 p-0\.5"/);
  assert.equal(workspaceSource.match(/compactToolbar && "h-7 px-2\.5 py-0 text-xs"/g)?.length, 2);
  assert.match(workspaceSource, /showProjectSelect\s*\?\s*\([\s\S]*?id="chat-project-select"/);
  assert.match(workspaceSource, /buildChatHref\(routeBasePath,/);
  assert.match(boundarySource, /getChatRouteRedirect/);
  assert.match(boundarySource, /router\.replace\(redirectHref, \{ scroll: false \}\)/);
});

test("chat page refreshes the conversation list after an execution completes", async () => {
  const [pageSource, conversationListSource] = await Promise.all([
    readFile(CHAT_WORKSPACE_URL, "utf8"),
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
    readFile(CHAT_WORKSPACE_URL, "utf8"),
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
  const [workspaceSource, routeSource] = await Promise.all([
    readFile(CHAT_WORKSPACE_URL, "utf8"),
    readFile(CHAT_ROUTE_URL, "utf8"),
  ]);

  assert.match(workspaceSource, /buildChatHref\(routeBasePath,/);
  assert.match(routeSource, /searchParams\.set\("projectId", params\.projectId\)/);
  assert.match(routeSource, /searchParams\.set\("contextId", params\.contextId\)/);
  assert.doesNotMatch(
    `${workspaceSource}\n${routeSource}`,
    /url-settings|hashSettingsValue|routeSettingsParam|settingsHash|hashchange/,
  );
});

test("chat page clears legacy settings URLs without restoring their values", async () => {
  const pageSource = await readFile(CHAT_WORKSPACE_URL, "utf8");

  assert.match(pageSource, /searchParams\.delete\("settings"\)/);
  assert.match(pageSource, /window\.history\.replaceState/);
  assert.doesNotMatch(pageSource, /decodeChatUrlSettings|getChatSettingsHashValue/);
});

test("chat page does not expose the share current URL action", async () => {
  const pageSource = await readFile(CHAT_WORKSPACE_URL, "utf8");

  assert.doesNotMatch(
    pageSource,
    /Share2|copyCurrentUrlToClipboard|handleShareCurrentUrl|Share current URL/,
  );
});

test("chat routes do not read or generate execution id query parameters", async () => {
  const [pageSource, conversationDetailsSource, jobLogsSource] = await Promise.all([
    readFile(CHAT_WORKSPACE_URL, "utf8"),
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
