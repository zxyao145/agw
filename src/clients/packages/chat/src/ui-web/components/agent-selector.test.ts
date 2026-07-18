import assert from "node:assert/strict";
import { readFile } from "node:fs/promises";
import test from "node:test";

const COMPONENT_URL = new URL("./agent-selector.tsx", import.meta.url);

test("AgentSelector loads agents and agentflows and returns a structured selection", async () => {
  const source = await readFile(COMPONENT_URL, "utf8").catch(() => "");

  assert.match(source, /export type AgentSelection =/);
  assert.match(source, /agentType: 0 \| 1;/);
  assert.match(source, /agentId: string;/);
  assert.match(source, /queryKey: \["agents"\]/);
  assert.match(source, /queryKey: \["agentflows"\]/);
  assert.match(source, /buildChatTargetOptions/);
  assert.match(source, /onSelect\(\{[\s\S]*agentType:[\s\S]*agentId:/);
});

test("AgentSelector supports an optional unassigned state", async () => {
  const source = await readFile(COMPONENT_URL, "utf8");

  assert.match(source, /clearable\?: boolean;/);
  assert.match(source, /placeholder\?: string;/);
  assert.match(source, /onClear\?: \(\) => void;/);
  assert.match(source, /if \(!target\) \{[\s\S]*onClear\?\.\(\)/);
  assert.match(source, /clearable=\{clearable\}/);
  assert.match(source, /placeholder=\{placeholder\}/);
});
