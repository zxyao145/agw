import assert from "node:assert/strict";
import test from "node:test";

test("buildSettingCommandPayload includes environment variables", async () => {
  const { buildSettingCommandPayload } = await import("./execution-ws" + ".ts");

  const payload = buildSettingCommandPayload({
    projectId: "project-1",
    taskId: "task-1",
    environmentVariables: {
      AGW_TOKEN: "secret",
    },
  });

  assert.equal(payload.type, "SettingCommand");
  assert.deepEqual(payload.environmentVariables, {
    AGW_TOKEN: "secret",
  });
});

test("buildSettingCommandPayload includes context id", async () => {
  const { buildSettingCommandPayload } = await import("./execution-ws" + ".ts");

  const payload = buildSettingCommandPayload({
    projectId: "project-1",
    taskId: "task-1",
    contextId: "context-1",
  });

  assert.equal(payload.contextId, "context-1");
});

test("buildSettingCommandPayload omits optional environment variables when absent", async () => {
  const { buildSettingCommandPayload } = await import("./execution-ws" + ".ts");

  const payload = buildSettingCommandPayload({
    projectId: "project-1",
    taskId: "task-1",
  });

  assert.equal("environmentVariables" in payload, false);
});
