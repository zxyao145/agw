import assert from "node:assert/strict";
import { readFile } from "node:fs/promises";
import test from "node:test";

const CHAT_URL = new URL("./chat.tsx", import.meta.url);
const CHAT_WORKSPACE_URL = new URL("../../pages/chat/chat-workspace.tsx", import.meta.url);
const RUNTIME_PACKAGE_URL = new URL(
  "../../../../../chat-runtime/src/conversation-controller.ts",
  import.meta.url,
);
const RUNTIME_HOOK_URL = new URL(
  "../../../../../chat-runtime/src/use-conversation-controller.ts",
  import.meta.url,
);

test("Web and Desktop pages keep Chat implementation inside workspace packages", async () => {
  const source = await readFile(CHAT_WORKSPACE_URL, "utf8");
  assert.match(source, /import \{ Chat, type ChatSessionSeed \}/);
  assert.match(source, /<Chat/);
});

test("shared Chat delegates presentation to chat-core", async () => {
  const source = await readFile(CHAT_URL, "utf8");
  assert.match(source, /buildConversationRenderModel/);
  assert.match(source, /<Conversation[\s\S]*?items=\{renderItems\}/);
  assert.doesNotMatch(source, /stripUsageContents/);
});

test("shared Chat uses the package runtime session manager", async () => {
  const source = await readFile(CHAT_URL, "utf8");
  assert.match(source, /executionSessionManager\.attach/);
  assert.match(source, /type ManagedExecutionHandle/);
  assert.doesNotMatch(source, /new ExecutionHubClient/);
});

test("checkpoint resume buffers new branch messages without using executionId as scope", async () => {
  const source = await readFile(CHAT_URL, "utf8");
  assert.match(source, /const checkpointResumeBufferRef = React\.useRef<AiMessage\[\] \| null>/);
  assert.match(source, /checkpointResumeBufferRef\.current = \[\]/);
  assert.match(source, /activeStreamingScopeRef\.current = null/);
  assert.match(source, /const resumedMessages = checkpointResumeBufferRef\.current \?\? \[\]/);
  assert.doesNotMatch(source, /activeStreamingScopeRef\.current = resumeExecutionId/);
});

test("incoming server scope wins over the local active fallback", async () => {
  const source = await readFile(CHAT_URL, "utf8");
  const ordinaryScope = source.lastIndexOf("getMessageStreamingScopeId(message) ??");
  const activeFallback = source.indexOf("activeStreamingScopeRef.current ??", ordinaryScope);
  assert.ok(ordinaryScope >= 0 && activeFallback > ordinaryScope);
});

test("auto-scroll state is shared through chat-core", async () => {
  const source = await readFile(CHAT_URL, "utf8");
  assert.match(source, /updateAutoScrollState/);
  assert.match(source, /onScroll=\{handleConversationScroll\}/);
  assert.match(source, /shouldAutoScroll/);
});

test("shared Chat prepends cursor pages without losing the visible scroll anchor", async () => {
  const source = await readFile(CHAT_URL, "utf8");

  assert.match(source, /getProjectConversationMessages/);
  assert.match(source, /direction: "older"/);
  assert.match(source, /pendingPrependAnchorRef/);
  assert.match(source, /scrollContainer\.scrollHeight - prependAnchor\.scrollHeight/);
  assert.match(source, /prependUniqueMessages/);
});

test("scroll-to-top exhausts older cursor pages before moving to the first message", async () => {
  const source = await readFile(CHAT_URL, "utf8");

  assert.match(source, /const handleScrollToTop = React\.useCallback\(async \(\) =>/);
  assert.match(source, /while \(hasMore && cursor\)/);
  assert.match(source, /pages\.reverse\(\)\.flat\(\)/);
  assert.match(source, /setMessages\(\(current\) =>/);
  assert.match(
    source,
    /requestAnimationFrame\(\(\) => \{[\s\S]*?requestAnimationFrame\(\(\) => \{[\s\S]*?scrollTo\(\{ top: 0, behavior: "auto" \}\)/,
  );
});

test("scroll to bottom reaches the full conversation scroll extent", async () => {
  const source = await readFile(CHAT_URL, "utf8");
  assert.match(
    source,
    /currentScrollContainer\?\.scrollTo\(\{[\s\S]*?top: currentScrollContainer\.scrollHeight[\s\S]*?behavior: "auto"/,
  );
  assert.match(
    source,
    /scrollToLatestMessage\(\);[\s\S]*?requestAnimationFrame\(\(\) => \{[\s\S]*?requestAnimationFrame\(scrollToLatestMessage\)/,
  );
});

test("chat-runtime exports a reusable conversation controller", async () => {
  const source = await readFile(RUNTIME_PACKAGE_URL, "utf8");
  const hookSource = await readFile(RUNTIME_HOOK_URL, "utf8");
  assert.match(source, /export class ConversationController/);
  assert.match(hookSource, /export function useConversationController/);
  for (const action of [
    "send",
    "stop",
    "hydrate",
    "clearRecords",
    "setMode",
    "setPermissionMode",
    "submitHumanResponse",
    "resumeCheckpoint",
    "dispose",
  ]) {
    assert.match(source, new RegExp(`\\b${action}\\b`));
  }
});
