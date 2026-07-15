import assert from "node:assert/strict";
import { readFile } from "node:fs/promises";
import test from "node:test";

const PANELS_URL = new URL("./capability-panels.tsx", import.meta.url);
const AGENT_FORM_URL = new URL(
  "../../app/(app)/(agents)/agents/components/agent-form-fields.tsx",
  import.meta.url,
);

async function readSource(url: URL, label: string) {
  try {
    return await readFile(url, "utf8");
  } catch (error) {
    assert.fail(`${label} is missing: ${String(error)}`);
  }
}

test("shared capability panels use SearchableSelect multi-selects for every capability", async () => {
  const source = await readSource(PANELS_URL, "shared capability panels");

  assert.match(source, /SearchableSelect,[\s\S]*type SearchableSelectOption/);
  assert.equal(source.match(/<SearchableSelect\s/g)?.length, 4);
  assert.equal(source.match(/multiple/g)?.length, 4);
  assert.match(source, /searchPlaceholder="Search skills\.\.\."/);
  assert.match(source, /searchPlaceholder="Search tools\.\.\."/);
  assert.match(source, /searchPlaceholder="Search MCP tool servers\.\.\."/);
  assert.match(source, /searchPlaceholder="Search connections\.\.\."/);
  assert.doesNotMatch(source, /<DropdownMenu/);
  assert.doesNotMatch(source, /<Popover/);
});

test("Agent form consumes all five shared panels while retaining Agent-only tabs and notices", async () => {
  const source = await readSource(AGENT_FORM_URL, "Agent form fields");

  assert.match(source, /from "@\/components\/definition-capabilities"/);
  assert.match(source, /<SkillsPanel/);
  assert.match(source, /<ToolsPanel/);
  assert.match(source, /<McpToolServersPanel/);
  assert.match(source, /<ConnectionsPanel/);
  assert.match(source, /<EnvironmentVariablesPanel/);
  assert.match(source, /<TabsTrigger value="system-prompt">Instructions<\/TabsTrigger>/);
  assert.match(source, /External agents do not support instructions configuration/);
  assert.match(source, /External agents do not support skill configuration/);
  assert.match(source, /External agents do not support tool configuration/);
});

test("Connection options expose ready-only searchable metadata", async () => {
  const source = await readSource(PANELS_URL, "shared capability panels");

  assert.match(source, /buildConnectionSelectOptions\(connectionOptions, selectedConnectionIds\)/);
  assert.match(source, /buildSelectedConnectionItems/);
});

test("Skills and Connections selected lists are built from selected IDs with removable fallbacks", async () => {
  const source = await readSource(PANELS_URL, "shared capability panels");

  assert.match(source, /buildSelectedSkillItems\(selectedSkillIds, skillsQuery\.data \?\? \[\]\)/);
  assert.match(source, /buildSelectedConnectionItems\(/);
  assert.match(source, /items=\{selectedSkills\}[\s\S]*onRemove=\{toggleSkill\}/);
  assert.match(source, /items=\{selectedConnections\}[\s\S]*onRemove=\{toggleConnection\}/);
});
