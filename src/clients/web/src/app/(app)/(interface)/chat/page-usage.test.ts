import assert from "node:assert/strict";
import { readFile } from "node:fs/promises";
import test from "node:test";

const CHAT_PAGE_URL = new URL("./page.tsx", import.meta.url);
const TASK_CLIENT_URL = new URL("../../../../api/task-client.ts", import.meta.url);

test("task client preserves normalized usage in context details", async () => {
  const taskClientSource = await readFile(TASK_CLIENT_URL, "utf8");

  assert.match(taskClientSource, /usage: TokenUsage;/);
  assert.match(taskClientSource, /usage\?: TokenUsageInput \| null;/);
  assert.match(taskClientSource, /usage: normalizeTokenUsage\(context\.usage\)/);
});

test("chat page initializes context usage and accumulates streamed usage before control messages", async () => {
  const pageSource = await readFile(CHAT_PAGE_URL, "utf8");
  const usageReadIndex = pageSource.indexOf("const messageUsage = getMessageTokenUsage(message)");
  const humanGateIndex = pageSource.indexOf("const humanGate = getPendingHumanGate(message)");
  const loadedUsageAssignments = pageSource.match(/setConversationUsage\(details\.usage\)/g) ?? [];

  assert.match(
    pageSource,
    /const \[conversationUsage, setConversationUsage\] =\s*React\.useState<TokenUsage>\(EMPTY_TOKEN_USAGE\)/,
  );
  assert.match(
    pageSource,
    /setConversationUsage\(\(current\) => addTokenUsage\(current, messageUsage\)\)/,
  );
  assert.notEqual(usageReadIndex, -1);
  assert.notEqual(humanGateIndex, -1);
  assert.ok(usageReadIndex < humanGateIndex);
  assert.equal(loadedUsageAssignments.length, 2);
  assert.match(pageSource, /setConversationUsage\(EMPTY_TOKEN_USAGE\)/);
});

test("chat page hides usage messages and renders compact token usage metrics", async () => {
  const pageSource = await readFile(CHAT_PAGE_URL, "utf8");

  assert.match(
    pageSource,
    /const visibleMessages = React\.useMemo\(\(\) => stripUsageContents\(messages\), \[messages\]\)/,
  );
  assert.match(pageSource, /<Conversation\s+messages=\{visibleMessages\}/);
  assert.match(pageSource, /<aside[\s\S]*?>[\s\S]*?Token usage/);
  assert.match(pageSource, /formatTokenCount\(\s*conversationUsage\.totalTokenCount,?\s*\)/);
  assert.match(pageSource, /formatTokenCount\(\s*conversationUsage\.inputTokenCount,?\s*\)/);
  assert.match(pageSource, /formatTokenCount\(\s*conversationUsage\.outputTokenCount,?\s*\)/);
  assert.match(pageSource, /formatTokenCount\(\s*conversationUsage\.cachedInputTokenCount,?\s*\)/);
  assert.match(pageSource, /formatTokenCount\(\s*conversationUsage\.reasoningTokenCount,?\s*\)/);
  assert.match(pageSource, />\s*Cached input\s*</);
  assert.match(pageSource, />\s*Reasoning\s*</);
});

test("chat page shows the usage panel only when its container reaches the lg width", async () => {
  const pageSource = await readFile(CHAT_PAGE_URL, "utf8");

  assert.match(pageSource, /<div className="@container relative h-full w-full">/);
  assert.match(pageSource, /<div className="h-full w-full overflow-y-auto">/);
  assert.match(pageSource, /<div className="relative flex min-h-full min-w-0 max-w-5xl flex-1">/);
  assert.match(pageSource, /<Conversation[\s\S]*?scrollable=\{false\}/);
  const usageAside = pageSource.match(
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
    pageSource,
    /<div className="pointer-events-none absolute inset-x-0 bottom-0 z-10 flex justify-center">/,
  );
});

test("chat page does not render the usage panel without visible messages", async () => {
  const pageSource = await readFile(CHAT_PAGE_URL, "utf8");

  assert.match(
    pageSource,
    /\{visibleMessages\.length > 0 \? \(\s*<aside[\s\S]*?aria-label="Current conversation token usage"[\s\S]*?<\/aside>\s*\) : null\}/,
  );
});
