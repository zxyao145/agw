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

test("Agents page wires immediate copy creation and query refresh", async () => {
  const [pageSource, tableSource] = await Promise.all([
    readFile(AGENTS_PAGE_URL, "utf8"),
    readFile(AGENTS_TABLE_URL, "utf8"),
  ]);

  assert.match(pageSource, /const copyAgentMutation = useMutation\(/);
  assert.match(pageSource, /createAgentCopyRequest\(agent, crypto\.randomUUID\(\)\)/);
  assert.match(pageSource, /apiPost\("\/api\/agents", \{ body \}\)/);
  assert.match(pageSource, /toast\.success\(`Agent .* copied`\)/);
  assert.match(pageSource, /invalidateQueries\(\{ queryKey: \["agents"\] \}\)/);
  assert.match(pageSource, /onCopy=\{handleCopy\}/);
  assert.match(pageSource, /isCopying=\{copyAgentMutation\.isPending\}/);

  assert.match(tableSource, /const isExternalAgent = agent\.type === 1/);
  assert.match(tableSource, /disabled=\{isCopyDisabled\}/);
  assert.match(tableSource, /External agents cannot be copied\./);
  assert.match(tableSource, /aria-label="Copy agent"/);
  assert.match(tableSource, /<Pencil[\s\S]*?<Copy[\s\S]*?<Trash2/);
});

test("Agentflows page loads graph details before creating and refreshing a copy", async () => {
  const [pageSource, tableSource] = await Promise.all([
    readFile(AGENTFLOWS_PAGE_URL, "utf8"),
    readFile(AGENTFLOWS_TABLE_URL, "utf8"),
  ]);

  assert.match(pageSource, /const copyAgentflowMutation = useMutation\(/);
  assert.match(pageSource, /fetchAgentflowDetails\(agentflow\.id\)/);
  assert.match(pageSource, /createAgentflowCopyRequest\(agentflow, details\)/);
  assert.match(pageSource, /apiPost\("\/api\/agentflows", \{ body \}\)/);
  assert.match(pageSource, /toast\.success\(`Agentflow .* copied`\)/);
  assert.match(pageSource, /invalidateQueries\(\{ queryKey: \["agentflows"\] \}\)/);
  assert.match(pageSource, /onCopy=\{handleCopyAgentflow\}/);
  assert.match(pageSource, /isCopying=\{copyAgentflowMutation\.isPending\}/);

  assert.match(tableSource, /onClick=\{\(\) => onCopy\(agentflow\)\}/);
  assert.match(tableSource, /disabled=\{isCopying\}/);
  assert.match(tableSource, /aria-label="Copy agentflow"/);
  assert.match(tableSource, /<Pencil[\s\S]*?<Copy[\s\S]*?<Trash2/);
});
