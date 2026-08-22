import assert from "node:assert/strict";
import test from "node:test";

import {
  buildSetModeCommand,
  buildSetPermissionModeCommand,
  buildExecCommand,
  buildInterruptCommand,
  buildSettingCommand,
  buildSubscribeExecutionCommand,
  DEFAULT_AGENT_MODE,
  executionReconnectDelaysMs,
  getAgentMode,
  getExecutionReconnectDelay,
  getLatestAgentMode,
  getTurnFinishedStatus,
  isModeControlMessage,
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
  assert.deepEqual(buildSetModeCommand("agent-1", "plan"), {
    type: "SetModeCommand",
    agentId: "agent-1",
    mode: "plan",
  });
  assert.deepEqual(buildSetPermissionModeCommand("alwaysAsk"), {
    type: "SetPermissionModeCommand",
    permissionMode: "alwaysAsk",
  });
  assert.deepEqual(buildSubscribeExecutionCommand("execution-1", "3-9"), {
    type: "SubscribeExecutionCommand",
    executionId: "execution-1",
    cursor: "3-9",
  });
});

test("shared agent mode helpers read live and persisted status messages", () => {
  const directStatus = {
    messageId: "mode-1",
    role: "system",
    contents: [],
    additionalProperties: { type: "mode-status", mode: "plan" },
  };
  const persistedStatus = {
    ...directStatus,
    messageId: "mode-2",
    additionalProperties: { type: "tool-mode-status", mode: "execute" },
  };

  assert.equal(getAgentMode(directStatus), "plan");
  assert.equal(getAgentMode(persistedStatus), "execute");
  assert.equal(getLatestAgentMode([directStatus, persistedStatus]), "execute");
  assert.equal(getLatestAgentMode([]), DEFAULT_AGENT_MODE);
  assert.equal(isModeControlMessage(directStatus), true);
  assert.equal(isModeControlMessage(persistedStatus), false);
  assert.equal(
    isModeControlMessage({
      ...directStatus,
      additionalProperties: { type: "mode-change-failed", mode: "plan" },
    }),
    true,
  );
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
