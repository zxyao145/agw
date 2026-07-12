import assert from "node:assert/strict";
import test from "node:test";

test("buildSettingCommand keeps target data out of settings", async () => {
  const { buildSettingCommand } = await import("./execution-hub" + ".ts");

  assert.deepEqual(
    buildSettingCommand({
      projectId: "project-1",
      contextId: "context-1",
      environmentVariables: { TOKEN: "value" },
    }),
    {
      type: "SettingCommand",
      projectId: "project-1",
      contextId: "context-1",
      environmentVariables: { TOKEN: "value" },
    },
  );
});

test("buildExecCommand includes target and streaming mode", async () => {
  const { buildExecCommand } = await import("./execution-hub" + ".ts");
  const input = { messageId: "message-1", author: "$agw", contents: [] };

  assert.deepEqual(buildExecCommand({ agentId: "agent-1", agentType: 0, stream: false, input }), {
    type: "ExecCommand",
    agentId: "agent-1",
    agentType: 0,
    stream: false,
    input,
  });
});

test("getTurnFinishedStatus reads terminal AgwMessage", async () => {
  const { getTurnFinishedStatus } = await import("./execution-hub" + ".ts");

  assert.equal(
    getTurnFinishedStatus({
      messageId: "message-1",
      role: "system",
      author: "$agw",
      contents: [],
      additionalProperties: { type: "turn-finished", status: "interrupted" },
    }),
    "interrupted",
  );
  assert.equal(
    getTurnFinishedStatus({
      messageId: "message-2",
      role: "assistant",
      author: "agent",
      contents: [],
    }),
    null,
  );
});
