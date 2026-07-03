import assert from "node:assert/strict";
import { readFile } from "node:fs/promises";
import test from "node:test";

const CONVERSATION_URL = new URL("./conversation.tsx", import.meta.url);

test("conversation renders agent name and author metadata above agent messages", async () => {
  const source = await readFile(CONVERSATION_URL, "utf8");

  assert.match(source, /function getAgentMessageMeta/);
  assert.match(source, /agentName/);
  assert.match(source, /agentAuthor/);
  assert.match(source, /\{agentMeta\.name\}/);
  assert.match(source, /\{agentMeta\.author\}/);
  assert.match(source, /AiMessageComponent message=\{item\.message\}/);
});
