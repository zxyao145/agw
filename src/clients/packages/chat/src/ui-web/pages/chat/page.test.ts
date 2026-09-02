import assert from "node:assert/strict";
import { readFile } from "node:fs/promises";
import test from "node:test";

const WEB_CHAT_PAGE_URL = new URL("./page.tsx", import.meta.url);
const CHAT_WORKSPACE_URL = new URL("./chat-workspace.tsx", import.meta.url);
const PACKAGES_URL = new URL("../../../../../", import.meta.url);
const DESKTOP_CHAT_PAGE_URL = new URL("../desktop-chat.tsx", import.meta.url);
const CHAT_ROUTE_BOUNDARY_URL = new URL(
  "../../components/chat/chat-route-boundary.tsx",
  import.meta.url,
);
const CHAT_ROUTE_URL = new URL("../../../lib/chat-route.ts", import.meta.url);
const CHAT_COMPONENT_URL = new URL("../../components/message/chat.tsx", import.meta.url);
const CONVERSATION_DETAILS_PAGE_URL = new URL(
  "projects/src/ui-web/pages/projects/conversations/details/page.tsx",
  PACKAGES_URL,
);
const JOB_LOGS_PAGE_URL = new URL("jobs/src/ui-web/pages/jobs/logs/page.tsx", PACKAGES_URL);
const CONVERSATION_LIST_URL = new URL(
  "projects/src/ui-web/components/task/conversation-list.tsx",
  PACKAGES_URL,
);
const TASK_CLIENT_URL = new URL("projects-core/src/task-client.ts", PACKAGES_URL);

test("Web and Desktop routes compose one shared Chat workspace", async () => {
  const [webPageSource, desktopPageSource, workspaceSource, boundarySource] = await Promise.all([
    readFile(WEB_CHAT_PAGE_URL, "utf8"),
    readFile(DESKTOP_CHAT_PAGE_URL, "utf8"),
    readFile(CHAT_WORKSPACE_URL, "utf8"),
    readFile(CHAT_ROUTE_BOUNDARY_URL, "utf8"),
  ]);

  assert.match(webPageSource, /<ChatWorkspace routeBasePath="\/chat" showProjectSelect\s*\/>/);
  assert.doesNotMatch(webPageSource, /compactToolbar/);
  assert.doesNotMatch(webPageSource, /showUserInputNavigation/);
  assert.match(
    desktopPageSource,
    /<ChatWorkspace[\s\S]*routeBasePath="\/desktop\/chat"[\s\S]*showProjectSelect=\{false\}[\s\S]*compactToolbar[\s\S]*showUserInputNavigation[\s\S]*\/>/,
  );
  assert.match(workspaceSource, /export function ChatWorkspace\(/);
  assert.match(workspaceSource, /compactToolbar\?: boolean/);
  assert.match(workspaceSource, /showUserInputNavigation\?: boolean/);
  assert.match(workspaceSource, /showUserInputNavigation=\{showUserInputNavigation\}/);
  assert.match(workspaceSource, /size=\{compactToolbar \? "sm" : "default"\}/);
  assert.match(workspaceSource, /compactToolbar && "h-8 p-2"/);
  assert.equal(workspaceSource.match(/compactToolbar && "h-6 px-2\.5 py-0 text-xs"/g)?.length, 2);
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
  assert.match(conversationListSource, /\[refreshSignal, refreshConversations\]/);
  assert.match(
    pageSource,
    /const \[conversationListRefreshSignal, setConversationListRefreshSignal\] = React\.useState\(0\)/,
  );
  assert.match(pageSource, /setConversationListRefreshSignal\(\(signal\) => signal \+ 1\)/);
  assert.match(pageSource, /refreshSignal=\{conversationListRefreshSignal\}/);
  assert.match(pageSource, /onConversationChange=\{refreshConversationList\}/);
});

test("new chat persists its conversation before selecting the local session", async () => {
  const workspaceSource = await readFile(CHAT_WORKSPACE_URL, "utf8");

  assert.match(workspaceSource, /const startNewConversation = React\.useCallback\(async/);
  assert.match(
    workspaceSource,
    /const conversation = await createProjectConversation\(selectedProjectId\)/,
  );
  assert.match(workspaceSource, /setConversationId\(conversation\.conversationId\)/);
  assert.match(workspaceSource, /setContextId\(conversation\.contextId\)/);
});

test("chat file explorer starts with diff mode disabled", async () => {
  const workspaceSource = await readFile(CHAT_WORKSPACE_URL, "utf8");

  assert.match(workspaceSource, /const \[onlyDiff, setOnlyDiff\] = React\.useState\(false\)/);
});

test("chat keeps a file's git scope while diff mode is disabled", async () => {
  const workspaceSource = await readFile(CHAT_WORKSPACE_URL, "utf8");
  const nonDiffBranchStart = workspaceSource.indexOf(
    "} else {",
    workspaceSource.indexOf("if (onlyDiff)"),
  );
  const nonDiffBranchEnd = workspaceSource.indexOf("} catch", nonDiffBranchStart);
  const nonDiffBranch = workspaceSource.slice(nonDiffBranchStart, nonDiffBranchEnd);

  assert.match(nonDiffBranch, /setSelectedDiffScope\(diffScope\)/);
  assert.doesNotMatch(nonDiffBranch, /setSelectedDiffScope\(undefined\)/);
  assert.match(workspaceSource, /setSelectedDiffScope\(targetScope\)/);
});

test("conversation toolbar owns the delete-all-history action and tooltip", async () => {
  const [workspaceSource, conversationListSource] = await Promise.all([
    readFile(CHAT_WORKSPACE_URL, "utf8"),
    readFile(CONVERSATION_LIST_URL, "utf8"),
  ]);

  const settingsDialogStart = workspaceSource.indexOf("function ChatSettingsDialog");
  const settingsDialogEnd = workspaceSource.indexOf("function ProjectRequiredState");
  const settingsDialogSource = workspaceSource.slice(settingsDialogStart, settingsDialogEnd);

  assert.doesNotMatch(settingsDialogSource, /Delete All History/);
  assert.doesNotMatch(settingsDialogSource, /onDeleteAllHistory/);
  assert.doesNotMatch(settingsDialogSource, /Chat History/);
  assert.doesNotMatch(settingsDialogSource, /conversationCount/);
  assert.match(conversationListSource, /aria-label="Delete All History"/);
  assert.match(conversationListSource, /<TooltipContent>Delete All History<\/TooltipContent>/);
  assert.match(conversationListSource, /onClick=\{\(\) => setClearAllDialogOpen\(true\)\}/);
  assert.match(conversationListSource, /className="cursor-pointer hover:text-destructive"/);
  assert.doesNotMatch(conversationListSource, /<Info /);
  assert.doesNotMatch(conversationListSource, /Chat History Storage/);
});

test("shared chat preserves streamed messages after an execution completes", async () => {
  const chatSource = await readFile(CHAT_COMPONENT_URL, "utf8");
  const terminalBranchStart = chatSource.indexOf("const terminalStatus =");
  const nextMessageBranchStart = chatSource.indexOf(
    "if (!isUserTurnMessage(message))",
    terminalBranchStart,
  );

  assert.notEqual(terminalBranchStart, -1);
  assert.notEqual(nextMessageBranchStart, -1);

  const terminalBranch = chatSource.slice(terminalBranchStart, nextMessageBranchStart);
  assert.doesNotMatch(terminalBranch, /getProjectConversationDetails|setMessages/);
});

test("conversation list ignores stale refresh responses", async () => {
  const conversationListSource = await readFile(CONVERSATION_LIST_URL, "utf8");

  assert.match(conversationListSource, /refreshRequestIdRef/);
  assert.match(conversationListSource, /requestId !== refreshRequestIdRef\.current/);
});

test("chat conversations use the shared friendly local date-time formatter", async () => {
  const conversationListSource = await readFile(CONVERSATION_LIST_URL, "utf8");

  assert.match(conversationListSource, /formatFriendlyLocalDateTime/);
  assert.match(
    conversationListSource,
    /formatFriendlyLocalDateTime\([\s\S]*?conversation\.updateTime \?\? conversation\.createTime/,
  );
  assert.doesNotMatch(conversationListSource, /const formatDate =/);
});

test("chat conversation list trusts the project conversation API visibility contract", async () => {
  const taskClientSource = await readFile(TASK_CLIENT_URL, "utf8");

  assert.match(taskClientSource, /return result\.map\(toConversationSummary\);/);
  assert.doesNotMatch(taskClientSource, /shouldIncludeConversation/);
});

test("chat page keeps conversation resource id separate from execution context id", async () => {
  const [pageSource, conversationListSource] = await Promise.all([
    readFile(CHAT_WORKSPACE_URL, "utf8"),
    readFile(CONVERSATION_LIST_URL, "utf8"),
  ]);

  assert.doesNotMatch(conversationListSource, new RegExp("current" + "Task" + "Id"));
  assert.doesNotMatch(conversationListSource, new RegExp("latest" + "Task" + "Id"));
  assert.match(conversationListSource, /conversation\.contextId === currentContextId/);
  assert.match(conversationListSource, /onActiveConversationResolved/);
  assert.match(pageSource, /currentContextId=\{contextId\}/);
  assert.match(pageSource, /currentConversationId=\{conversationId\}/);
  assert.match(pageSource, /setContextId\(details\.contextId\)/);
  assert.match(pageSource, /setConversationId\(conversation\.conversationId\)/);
  assert.match(pageSource, /conversationId=\{conversationId\}/);
  assert.match(pageSource, /onContextIdChange=\{handleChatContextIdChange\}/);
  assert.match(pageSource, /syncRoute\(selectedProjectId, conversation\.conversationId\)/);
});

test("chat routes keep project and conversation parameters without URL settings", async () => {
  const [workspaceSource, routeSource] = await Promise.all([
    readFile(CHAT_WORKSPACE_URL, "utf8"),
    readFile(CHAT_ROUTE_URL, "utf8"),
  ]);

  assert.match(workspaceSource, /buildChatHref\(routeBasePath,/);
  assert.match(routeSource, /searchParams\.set\("projectId", params\.projectId\)/);
  assert.match(routeSource, /searchParams\.set\("conversationId", params\.conversationId\)/);
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

test("chat history load errors use conversation terminology", async () => {
  const workspaceSource = await readFile(CHAT_WORKSPACE_URL, "utf8");

  assert.match(workspaceSource, /Failed to load conversation:/);
  assert.doesNotMatch(workspaceSource, /Failed to load context:/);
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
