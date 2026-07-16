import assert from "node:assert/strict";
import { readFile } from "node:fs/promises";
import test from "node:test";

const CONVERSATION_URL = new URL("./conversation.tsx", import.meta.url);

test("conversation renders agent name and author metadata above agent messages", async () => {
  const source = await readFile(CONVERSATION_URL, "utf8");

  assert.match(source, /function getMessageMeta/);
  assert.match(source, /agentName/);
  assert.match(source, /agentAuthor/);
  assert.match(source, /\{messageMeta\.name\}/);
  assert.match(source, /\{messageMeta\.author\}/);
  assert.match(source, /AiMessageComponent message=\{item\.message\}/);
});

test("conversation renders user author metadata above and aligned with user messages", async () => {
  const source = await readFile(CONVERSATION_URL, "utf8");

  assert.doesNotMatch(source, /message\.role === "user" \|\| isResultMessage\(message\)/);
  assert.match(source, /if \(message\.role === "user"\)[\s\S]*?name: null,[\s\S]*?author:/);
  assert.match(source, /const isUserMessage = item\.message\.role === "user";/);
  assert.match(source, /isUserMessage \? "ml-auto justify-end" : ""/);
});

test("conversation can delegate scrolling while keeping messages centered", async () => {
  const source = await readFile(CONVERSATION_URL, "utf8");

  assert.match(source, /scrollable\?: boolean;/);
  assert.match(source, /scrollable = true,/);
  assert.match(source, /scrollable && "overflow-y-auto"/);
  assert.match(source, /<div className="mx-auto w-full max-w-225 space-y-4 pb-36">/);
});

test("conversation renders authorless system messages after collapsing consecutive entries", async () => {
  const source = await readFile(CONVERSATION_URL, "utf8");

  assert.match(source, /collapseConsecutiveSystemMessages\(messages\)/);
  assert.match(source, /!currentMsg\.author && currentMsg\.role !== "system"/);
  assert.doesNotMatch(source, /if \(currentMsg\.role === "system"\) \{\s*continue;/);
  assert.match(source, /if \(isResult\)[\s\S]*?continue;/);
});

test("function results only pair with calls from the same streaming scope", async () => {
  const source = await readFile(CONVERSATION_URL, "utf8");

  assert.match(
    source,
    /resultCallId === callId &&[\s\S]*?msg\.streamingScopeId === currentMsg\.streamingScopeId/,
  );
});

test("duplicate call ids produce one tool group per turn", async () => {
  const { defaultProcessMessages } = await loadMessageProcessor();
  const items = defaultProcessMessages([
    toolMessage("FunctionCallContent", "user-1"),
    toolMessage("FunctionResultContent", "user-1"),
    toolMessage("FunctionCallContent", "user-2"),
    toolMessage("FunctionResultContent", "user-2"),
  ]);

  assert.equal(items.length, 2);
  assert.deepEqual(
    items.map((item: { type: string }) => item.type),
    ["accordion", "accordion"],
  );
  assert.deepEqual(
    items.map((item: { messages: Array<{ streamingScopeId: string }> }) =>
      item.messages.map((message) => message.streamingScopeId),
    ),
    [
      ["user-1", "user-1"],
      ["user-2", "user-2"],
    ],
  );
});
