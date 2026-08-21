import assert from "node:assert/strict";
import test from "node:test";

import {
  buildExecCommand,
  buildInterruptCommand,
  buildSettingCommand,
  buildSubscribeExecutionCommand,
  executionReconnectDelaysMs,
  getExecutionReconnectDelay,
  getTurnFinishedStatus,
} from "./protocol";

test("shared execution commands match the server contract", () => {
  const input = { messageId: "message-1", author: "$agw", contents: [] };

  assert.deepEqual(
    buildSettingCommand({
      projectId: "project-1",
      contextId: null,
      environmentVariables: { TOKEN: "value" },
      permissionMode: "fullAccess",
    }),
    {
      type: "SettingCommand",
      projectId: "project-1",
      contextId: null,
      environmentVariables: { TOKEN: "value" },
      permissionMode: "fullAccess",
    },
  );
  assert.deepEqual(
    buildExecCommand({
      agentId: "agent-1",
      agentType: 0,
      executionId: "execution-1",
      input,
    }),
    {
      type: "ExecCommand",
      agentId: "agent-1",
      agentType: 0,
      executionId: "execution-1",
      stream: true,
      input,
    },
  );
  assert.deepEqual(buildInterruptCommand("execution-1", "stop"), {
    type: "InterruptCommand",
    executionId: "execution-1",
    reason: "stop",
  });
  assert.deepEqual(buildSubscribeExecutionCommand("execution-1", "3-9"), {
    type: "SubscribeExecutionCommand",
    executionId: "execution-1",
    cursor: "3-9",
  });
});

test("turn-finished is message-level and accepts only server statuses", () => {
  const message = {
    messageId: "message-1",
    role: "system",
    author: "$agw",
    contents: [],
    additionalProperties: { type: "turn-finished", status: "failed" },
  };

  assert.equal(getTurnFinishedStatus(message), "failed");
  assert.equal(
    getTurnFinishedStatus({
      ...message,
      additionalProperties: undefined,
      contents: [{ type: "TextContent", additionalProperties: { type: "turn-finished" } }],
    }),
    null,
  );
  assert.equal(
    getTurnFinishedStatus({
      ...message,
      additionalProperties: { type: "turn-finished", status: "cancelled" },
    }),
    "completed",
  );
});

test("shared reconnect delays stop after the configured attempts", () => {
  assert.deepEqual(
    [...executionReconnectDelaysMs],
    [0, 2_000, 5_000, 7_000, 10_000, 20_000, 30_000],
  );
  assert.equal(getExecutionReconnectDelay(0), 0);
  assert.equal(getExecutionReconnectDelay(executionReconnectDelaysMs.length), null);
});
