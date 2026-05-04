import assert from "node:assert/strict";
import test from "node:test";

test("buildSettingCommandPayload includes environment variables", async () => {
  const { buildSettingCommandPayload } = await import("./execution-ws" + ".ts");

  const payload = buildSettingCommandPayload({
    projectId: "project-1",
    taskId: "task-1",
    settingContent: "{}",
    workspace: "/tmp/workspace",
    environmentVariables: {
      AGW_TOKEN: "secret",
    },
  });

  assert.equal(payload.type, "SettingCommand");
  assert.equal(payload.workspace, "/tmp/workspace");
  assert.deepEqual(payload.environmentVariables, {
    AGW_TOKEN: "secret",
  });
});

test("buildSettingCommandPayload omits optional workspace and environment variables when absent", async () => {
  const { buildSettingCommandPayload } = await import("./execution-ws" + ".ts");

  const payload = buildSettingCommandPayload({
    projectId: "project-1",
    taskId: "task-1",
    settingContent: "{}",
  });

  assert.equal("workspace" in payload, false);
  assert.equal("environmentVariables" in payload, false);
});
