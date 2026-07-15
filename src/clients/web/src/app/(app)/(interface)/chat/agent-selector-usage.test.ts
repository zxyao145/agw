import assert from "node:assert/strict";
import { readFile } from "node:fs/promises";
import test from "node:test";

const PAGE_URL = new URL("./page.tsx", import.meta.url);

test("Chat uses AgentSelector and maps its structured selection to the existing target value", async () => {
  const source = await readFile(PAGE_URL, "utf8");

  assert.match(source, /import \{ AgentSelector/);
  assert.match(source, /<AgentSelector/);
  assert.match(source, /projectId=\{selectedProjectId\}/);
  assert.match(source, /onSelect=\{handleAgentSelect\}/);
  assert.doesNotMatch(source, /id="chat-target-select"[\s\S]{0,120}onValueChange=/);
});
