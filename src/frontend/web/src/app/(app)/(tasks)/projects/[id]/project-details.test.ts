import assert from "node:assert/strict";
import { readFile } from "node:fs/promises";
import test from "node:test";

const PAGE_URL = new URL("./page.tsx", import.meta.url);
const PROJECT_DETAILS_MODULE_URL = new URL("./project-details.ts", import.meta.url);

async function importProjectDetailsModule() {
  try {
    return await import(PROJECT_DETAILS_MODULE_URL.href);
  } catch (error) {
    assert.fail(`project-details module is missing or invalid: ${String(error)}`);
  }
}

test("buildQuickTaskJobRequest creates an enabled one-shot job scheduled immediately", async () => {
  const { QUICK_TASK_TRIGGER_TYPE, buildQuickTaskJobRequest } = await importProjectDetailsModule();
  const now = new Date("2026-04-09T10:20:30.000Z");

  assert.deepEqual(
    buildQuickTaskJobRequest(
      {
        id: "11111111-1111-1111-1111-000000000001",
        name: "Alpha Project",
      },
      now,
    ),
    {
      projectId: "11111111-1111-1111-1111-000000000001",
      agentType: null,
      agentId: null,
      name: "Alpha Project - Quick Task - 2026-04-09T10:20:30Z",
      prompt: null,
      triggerType: QUICK_TASK_TRIGGER_TYPE,
      triggerValue: "2026-04-09T10:20:40.000Z",
      maxRetryCount: 0,
      isEnabled: true,
    },
  );
});

test("getProjectDetailItems keeps the existing project detail fields and placeholders", async () => {
  const { getProjectDetailItems } = await importProjectDetailsModule();

  assert.deepEqual(
    getProjectDetailItems({
      description: "  Demo project  ",
      workspace: "",
      extraSetting: null,
    }),
    [
      { label: "Description", value: "Demo project" },
      { label: "Workspace", value: "-", mono: true },
      { label: "Extra Setting", value: "-", mono: true },
    ],
  );
});

test("project page wires the details dialog and quick-create actions through shared labels", async () => {
  const { CREATE_TASK_BUTTON_LABEL, DETAILS_BUTTON_LABEL, PROJECT_DETAILS_DIALOG_TITLE } =
    await importProjectDetailsModule();
  const source = await readFile(PAGE_URL, "utf8");

  assert.equal(DETAILS_BUTTON_LABEL, "Details");
  assert.equal(CREATE_TASK_BUTTON_LABEL, "Create Task");
  assert.equal(PROJECT_DETAILS_DIALOG_TITLE, "Project Details");

  assert.match(source, /DETAILS_BUTTON_LABEL/);
  assert.match(source, /CREATE_TASK_BUTTON_LABEL/);
  assert.match(source, /PROJECT_DETAILS_DIALOG_TITLE/);
});
