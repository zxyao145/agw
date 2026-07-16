import assert from "node:assert/strict";
import { readFile } from "node:fs/promises";
import test from "node:test";

const CHAT_COMPONENT_URL = new URL("../../../../components/message/chat.tsx", import.meta.url);
const CHAT_ASIDE_COMPONENT_URL = new URL(
  "../../../../components/message/chat-aside.tsx",
  import.meta.url,
);
const TASK_CLIENT_URL = new URL("../../../../api/task-client.ts", import.meta.url);

test("task client preserves normalized usage in context details", async () => {
  const taskClientSource = await readFile(TASK_CLIENT_URL, "utf8");

  assert.match(taskClientSource, /usage: TokenUsage;/);
  assert.match(taskClientSource, /usage\?: TokenUsageInput \| null;/);
  assert.match(taskClientSource, /usage: normalizeTokenUsage\(context\.usage\)/);
});

test("shared chat initializes seeded usage and accumulates streamed usage before control messages", async () => {
  const chatSource = await readFile(CHAT_COMPONENT_URL, "utf8");
  const usageReadIndex = chatSource.indexOf("const messageUsage = getMessageTokenUsage(message)");
  const humanGateIndex = chatSource.indexOf("const humanGate = getPendingHumanGate(message)");

  assert.match(
    chatSource,
    /const \[conversationUsage, setConversationUsage\] = React\.useState<TokenUsage>\(sessionSeed\.usage\)/,
  );
  assert.match(
    chatSource,
    /setConversationUsage\(\(current\) => addTokenUsage\(current, messageUsage\)\)/,
  );
  assert.notEqual(usageReadIndex, -1);
  assert.notEqual(humanGateIndex, -1);
  assert.ok(usageReadIndex < humanGateIndex);
  assert.match(chatSource, /setConversationUsage\(sessionSeed\.usage\)/);
  assert.match(chatSource, /setConversationUsage\(EMPTY_TOKEN_USAGE\)/);
});

test("shared chat hides usage messages and renders compact token usage metrics", async () => {
  const [chatSource, chatAsideSource] = await Promise.all([
    readFile(CHAT_COMPONENT_URL, "utf8"),
    readFile(CHAT_ASIDE_COMPONENT_URL, "utf8").catch(() => ""),
  ]);

  assert.match(
    chatSource,
    /const visibleMessages = React\.useMemo\(\(\) => stripUsageContents\(messages\), \[messages\]\)/,
  );
  assert.match(chatSource, /<Conversation\s+messages=\{visibleMessages\}/);
  assert.match(chatSource, /import \{ ChatAside \} from "@\/components\/message\/chat-aside"/);
  assert.match(chatSource, /<ChatAside usage=\{conversationUsage\} \/>/);
  assert.match(chatAsideSource, /<aside[\s\S]*?>[\s\S]*?Token usage/);
  assert.match(chatAsideSource, /formatTokenCount\(\s*usage\.totalTokenCount,?\s*\)/);
  assert.match(chatAsideSource, /formatTokenCount\(\s*usage\.inputTokenCount,?\s*\)/);
  assert.match(chatAsideSource, /formatTokenCount\(\s*usage\.outputTokenCount,?\s*\)/);
  assert.match(chatAsideSource, /formatTokenCount\(\s*usage\.cachedInputTokenCount,?\s*\)/);
  assert.match(chatAsideSource, /formatTokenCount\(\s*usage\.reasoningTokenCount,?\s*\)/);
  assert.match(chatAsideSource, />\s*Cached input\s*</);
  assert.match(chatAsideSource, />\s*Reasoning\s*</);
});

test("shared chat shows the usage panel only when its container reaches the lg width", async () => {
  const [chatSource, chatAsideSource] = await Promise.all([
    readFile(CHAT_COMPONENT_URL, "utf8"),
    readFile(CHAT_ASIDE_COMPONENT_URL, "utf8").catch(() => ""),
  ]);

  assert.match(chatSource, /cn\("@container relative h-full min-h-0 w-full overflow-hidden"/);
  assert.match(chatSource, /<div className="h-full w-full overflow-y-auto">/);
  assert.match(chatSource, /<div className="relative flex min-h-full min-w-0 max-w-5xl flex-1">/);
  assert.match(chatSource, /<Conversation[\s\S]*?scrollable=\{false\}/);
  const usageAside = chatAsideSource.match(
    /<aside\s+className="([^"]+)"\s+aria-label="Current conversation token usage"/,
  );
  assert.ok(usageAside);
  const usageAsideClasses = usageAside[1].split(" ");
  assert.ok(usageAsideClasses.includes("hidden"));
  assert.ok(usageAsideClasses.includes("sticky"));
  assert.ok(usageAsideClasses.includes("top-0"));
  assert.ok(usageAsideClasses.includes("@min-[64rem]:block"));
  assert.ok(!usageAsideClasses.includes("lg:block"));
  assert.match(
    chatSource,
    /<div className="pointer-events-none absolute inset-x-0 bottom-0 z-10 flex justify-center">/,
  );
});

test("shared chat does not render the usage panel without visible messages", async () => {
  const chatSource = await readFile(CHAT_COMPONENT_URL, "utf8");

  assert.match(
    chatSource,
    /\{visibleMessages\.length > 0 \? \(?\s*<ChatAside usage=\{conversationUsage\} \/>\s*\)? : null\}/,
  );
});
