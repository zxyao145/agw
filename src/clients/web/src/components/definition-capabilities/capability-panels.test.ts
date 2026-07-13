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

test("shared capability panels keep dialog portals and wheel-safe scroll regions", async () => {
  const source = await readSource(PANELS_URL, "shared capability panels");

  assert.equal(source.match(/<DropdownMenu modal=\{false\}>/g)?.length, 3);
  assert.equal(source.match(/portalContainer=\{dialogPortalContainer\}/g)?.length, 4);
  assert.equal(source.match(/max-h-72 overflow-y-auto/g)?.length, 4);
  assert.match(source, /<Popover[^>]*modal=\{false\}/);
});

test("Agent form consumes all five shared panels while retaining Agent-only tabs and notices", async () => {
  const source = await readSource(AGENT_FORM_URL, "Agent form fields");

  assert.match(source, /from "@\/components\/definition-capabilities"/);
  assert.match(source, /<SkillsPanel/);
  assert.match(source, /<ToolsPanel/);
  assert.match(source, /<McpToolServersPanel/);
  assert.match(source, /<AppsPanel/);
  assert.match(source, /<EnvironmentVariablesPanel/);
  assert.match(source, /<TabsTrigger value="system-prompt">System Prompt<\/TabsTrigger>/);
  assert.match(source, /External agents do not support system prompt configuration/);
  assert.match(source, /External agents do not support skill configuration/);
  assert.match(source, /External agents do not support tool configuration/);
});

test("App options use button selection semantics without nested interactive controls", async () => {
  const source = await readSource(PANELS_URL, "shared capability panels");

  assert.doesNotMatch(source, /<input[\s\S]*type="checkbox"/);
  assert.match(source, /aria-pressed=\{selectedAppInstanceIds\.includes\(app\.id\)\}/);
  assert.match(source, /<Check[^>]*aria-hidden/);
});

test("Skills and Apps selected lists are built from selected IDs with removable fallbacks", async () => {
  const source = await readSource(PANELS_URL, "shared capability panels");

  assert.match(source, /buildSelectedSkillItems\(selectedSkillIds, skillsQuery\.data \?\? \[\]\)/);
  assert.match(source, /buildSelectedAppItems\(selectedAppInstanceIds, appOptions\)/);
  assert.match(source, /items=\{selectedSkills\}[\s\S]*onRemove=\{toggleSkill\}/);
  assert.match(source, /items=\{selectedApps\}[\s\S]*onRemove=\{toggleAppInstance\}/);
});
