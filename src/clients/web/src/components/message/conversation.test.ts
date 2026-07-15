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
