import assert from "node:assert/strict";
import { readFile } from "node:fs/promises";
import test from "node:test";

const PICKER_URL = new URL("./project-picker.tsx", import.meta.url);

test("Desktop Project picker is searchable and exposes explicit query states", async () => {
  const source = await readFile(PICKER_URL, "utf8");

  assert.match(source, /className="agw-titlebar-button"/);
  assert.match(source, /aria-label="Open project"/);
  assert.match(source, /placeholder="Search projects…"/);
  assert.match(source, /project\.name[\s\S]*?project\.workspace[\s\S]*?project\.id/);
  assert.match(source, /Loading projects…/);
  assert.match(source, /No projects available/);
  assert.match(source, /No matching projects/);
  assert.match(source, /errorMessage/);
});

test("Desktop Project picker selects an item and closes the popover", async () => {
  const source = await readFile(PICKER_URL, "utf8");

  assert.match(source, /onSelect\(projectId\)/);
  assert.match(source, /setOpen\(false\)/);
  assert.match(source, /aria-selected=\{project\.id === activeProjectId\}/);
  assert.match(source, /project\.workspace/);
});

test("Desktop Project picker exposes an inline quick-create form", async () => {
  const source = await readFile(PICKER_URL, "utf8");

  assert.match(source, /aria-label="Create project"/);
  assert.match(source, /setCreateMode\(true\)/);
  assert.match(source, /<form[\s\S]*onSubmit=\{handleCreate\}/);
  assert.match(source, /id="desktop-project-name"/);
  assert.match(source, /id="desktop-project-workspace"/);
  assert.match(source, /Creating…/);
});

test("Desktop quick-create handles defaults, success, errors, and cancellation", async () => {
  const source = await readFile(PICKER_URL, "utf8");

  assert.match(source, /formatProjectFolderName\(createName\)/);
  assert.match(source, /syncDefaultProjectWorkspace\(/);
  assert.match(source, /resolveCreateProjectWorkspace\(normalizedName, createWorkspace\)/);
  assert.match(source, /await onCreate\(\{[\s\S]*name: normalizedName,[\s\S]*workspace:/);
  assert.match(source, /getApiErrorMessage\(error\)/);
  assert.match(source, /role="alert"/);
  assert.match(source, /event\.key === "Escape"/);
  assert.match(source, /onEscapeKeyDown=\{\(event\) => \{/);
  assert.match(source, /resetCreateForm\(\)[\s\S]*setOpen\(false\)/);
});
