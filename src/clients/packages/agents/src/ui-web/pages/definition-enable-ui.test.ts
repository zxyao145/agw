import assert from "node:assert/strict";
import { readFile } from "node:fs/promises";
import test from "node:test";

const AGENTS_PAGE_URL = new URL("./agents/page.tsx", import.meta.url);
const AGENTS_TABLE_URL = new URL("./agents/components/agents-table.tsx", import.meta.url);
const AGENTFLOWS_PAGE_URL = new URL("./agentflows/page.tsx", import.meta.url);
const AGENTFLOWS_TABLE_URL = new URL(
  "./agentflows/components/agentflows-table.tsx",
  import.meta.url,
);
const AGENTFLOW_BUILDER_URL = new URL(
  "./agentflows/components/visual-agentflow-builder.tsx",
  import.meta.url,
);

test("definition management tables expose enabled switches backed by dedicated endpoints", async () => {
  const [agentsPage, agentsTable, agentflowsPage, agentflowsTable] = await Promise.all([
    readFile(AGENTS_PAGE_URL, "utf8"),
    readFile(AGENTS_TABLE_URL, "utf8"),
    readFile(AGENTFLOWS_PAGE_URL, "utf8"),
    readFile(AGENTFLOWS_TABLE_URL, "utf8"),
  ]);

  assert.match(agentsPage, /apiPut\("\/api\/agents\/enabled"/);
  assert.match(agentsTable, /checked=\{agent\.enable\}/);
  assert.match(agentsTable, /onCheckedChange=\{\(enable\) => onEnabledChange\(agent, enable\)\}/);
  assert.match(agentflowsPage, /apiPut\("\/api\/agentflows\/enabled"/);
  assert.match(agentflowsTable, /checked=\{agentflow\.enable\}/);
  assert.match(
    agentflowsTable,
    /onCheckedChange=\{\(enable\) => onEnabledChange\(agentflow, enable\)\}/,
  );
});

test("agentflow builder target selects exclude disabled definitions", async () => {
  const source = await readFile(AGENTFLOW_BUILDER_URL, "utf8");

  assert.match(source, /agents\.filter\(\(agent\) => agent\.enable\)/);
  assert.match(source, /agentflows\.filter\(\(agentflow\) => agentflow\.enable\)/);
});
