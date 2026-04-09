import assert from "node:assert/strict";
import test from "node:test";

const PROJECT_DETAILS_MODULE_URL = new URL("./project-details.ts", import.meta.url);

async function importProjectDetailsModule() {
  try {
    return await import(PROJECT_DETAILS_MODULE_URL.href);
  } catch (error) {
    assert.fail(`project-details module is missing or invalid: ${String(error)}`);
  }
}

test("createDefaultTaskJobName returns the expected timestamp-and-random shape", async () => {
  const { createDefaultTaskJobName } = await importProjectDetailsModule();
  const now = new Date("2026-04-09T11:22:33.000Z");

  assert.equal(createDefaultTaskJobName(now, 4821), "Job-20260409-112233-4821");
});

test("buildCreateTaskJobRequest maps an agent target to agentType 0", async () => {
  const { QUICK_TASK_TRIGGER_TYPE, buildCreateTaskJobRequest } = await importProjectDetailsModule();
  const now = new Date("2026-04-09T10:20:30.000Z");

  assert.deepEqual(
    buildCreateTaskJobRequest({
      projectId: "11111111-1111-1111-1111-000000000001",
      targetValue: "agent:agent-1",
      jobName: "  Job-20260409-102030-4821  ",
      prompt: "  Summarize recent work  ",
      now,
    }),
    {
      projectId: "11111111-1111-1111-1111-000000000001",
      agentType: 0,
      agentId: "agent-1",
      name: "Job-20260409-102030-4821",
      prompt: "Summarize recent work",
      triggerType: QUICK_TASK_TRIGGER_TYPE,
      triggerValue: "2026-04-09T10:20:40.000Z",
      maxRetryCount: 0,
      isEnabled: true,
    },
  );
});

test("buildCreateTaskJobRequest maps an agentflow target to agentType 1", async () => {
  const { QUICK_TASK_TRIGGER_TYPE, buildCreateTaskJobRequest } =
    await importProjectDetailsModule();
  const now = new Date("2026-04-09T10:20:30.000Z");

  assert.deepEqual(
    buildCreateTaskJobRequest({
      projectId: "11111111-1111-1111-1111-000000000001",
      targetValue: "agentflow:flow-7",
      jobName: "  Job-20260409-102030-4821  ",
      prompt: "Run the workflow",
      now,
    }),
    {
      projectId: "11111111-1111-1111-1111-000000000001",
      agentType: 1,
      agentId: "flow-7",
      name: "Job-20260409-102030-4821",
      prompt: "Run the workflow",
      triggerType: QUICK_TASK_TRIGGER_TYPE,
      triggerValue: "2026-04-09T10:20:40.000Z",
      maxRetryCount: 0,
      isEnabled: true,
    },
  );
});
