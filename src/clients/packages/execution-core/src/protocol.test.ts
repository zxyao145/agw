import assert from "node:assert/strict";
import test from "node:test";
import type { InteractionResponse } from "./protocol";

import {
  buildHumanResponseCommand,
  buildSetModeCommand,
  buildSetPermissionModeCommand,
  buildExecCommand,
  buildInterruptCommand,
  buildResumeCheckpointCommand,
  buildSettingCommand,
  buildSubscribeExecutionCommand,
  DEFAULT_AGENT_MODE,
  executionReconnectDelaysMs,
  getAgentMode,
  getExecutionReconnectDelay,
  getLatestAgentMode,
  getTurnFinishedStatus,
  getTurnIdentity,
  getTurnPosition,
  isModeControlMessage,
  isSupersededTurnMessage,
  isTurnStartMessage,
  TURN_FINISHED_MESSAGE_TYPE,
  TURN_START_MESSAGE_TYPE,
} from "./protocol";

const interactionResponses: InteractionResponse[] = [
  ...(["Once", "AlwaysTool", "AlwaysArguments"] as const).map((scope) => ({
    kind: "tool-approval" as const,
    interactionId: "tool-1",
    approved: true,
    scope,
  })),
  { kind: "tool-approval", interactionId: "tool-1", approved: false, scope: "Once" },
  { kind: "workflow-gate", interactionId: "gate-1", approved: true, responseText: "Continue" },
  { kind: "workflow-gate", interactionId: "gate-1", approved: false },
  {
    kind: "user-input",
    interactionId: "input-1",
    cancelled: false,
    responseData: { confirmed: false },
  },
  { kind: "user-input", interactionId: "input-1", cancelled: false, responseData: { value: "" } },
  { kind: "user-input", interactionId: "input-1", cancelled: true },
];

for (const [index, response] of interactionResponses.entries()) {
  test(`human response ${index} preserves its discriminated nested wire shape`, () => {
    assert.deepEqual(buildHumanResponseCommand({ executionId: "execution-1", response }), {
      type: "HumanResponseCommand",
      executionId: "execution-1",
      response,
    });
    assert.deepEqual(buildHumanResponseCommand({ response }), {
      type: "HumanResponseCommand",
      response,
    });
  });
}

test("shared execution commands match the server contract", () => {
  const input = { messageId: "message-1", author: "$agw", contents: [] };

  assert.deepEqual(
    buildSettingCommand({
      projectId: "project-1",
      conversationId: "conversation-1",
      environmentVariables: { TOKEN: "value" },
      permissionMode: "fullAccess",
    }),
    {
      type: "SettingCommand",
      projectId: "project-1",
      conversationId: "conversation-1",
      environmentVariables: { TOKEN: "value" },
      permissionMode: "fullAccess",
    },
  );
  assert.deepEqual(
    buildSettingCommand({
      projectId: "project-1",
      conversationId: "conversation-1",
      resultOnly: true,
    }),
    {
      type: "SettingCommand",
      projectId: "project-1",
      conversationId: "conversation-1",
      resultOnly: true,
    },
  );
  assert.deepEqual(
    buildSettingCommand({ projectId: "project-1", conversationId: "conversation-1" }),
    {
      type: "SettingCommand",
      projectId: "project-1",
      conversationId: "conversation-1",
    },
  );
  assert.deepEqual(
    buildExecCommand({
      conversationId: "conversation-1",
      agentId: "agent-1",
      agentType: 0,
      executionId: "execution-1",
      input,
    }),
    {
      type: "ExecCommand",
      conversationId: "conversation-1",
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
  assert.deepEqual(
    buildHumanResponseCommand({
      executionId: "execution-1",
      response: {
        kind: "user-input",
        interactionId: "request-1",
        cancelled: false,
        responseData: { answers: { Choice: "A" } },
      },
    }),
    {
      type: "HumanResponseCommand",
      executionId: "execution-1",
      response: {
        kind: "user-input",
        interactionId: "request-1",
        cancelled: false,
        responseData: { answers: { Choice: "A" } },
      },
    },
  );
  assert.deepEqual(
    buildResumeCheckpointCommand({
      checkpointOccurrenceId: "checkpoint-1",
      resumeExecutionId: "execution-2",
      agentflowId: "agentflow-1",
    }),
    {
      type: "ResumeCheckpointCommand",
      checkpointOccurrenceId: "checkpoint-1",
      resumeExecutionId: "execution-2",
      agentflowId: "agentflow-1",
    },
  );
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
    additionalProperties: { type: TURN_FINISHED_MESSAGE_TYPE, status: "failed" },
  };

  assert.equal(TURN_FINISHED_MESSAGE_TYPE, "agw-turn-finished");
  assert.equal(getTurnFinishedStatus(message), "failed");
  assert.equal(
    getTurnFinishedStatus({
      ...message,
      additionalProperties: undefined,
      contents: [
        { type: "TextContent", additionalProperties: { type: TURN_FINISHED_MESSAGE_TYPE } },
      ],
    }),
    null,
  );
  assert.equal(
    getTurnFinishedStatus({
      ...message,
      additionalProperties: { type: TURN_FINISHED_MESSAGE_TYPE, status: "cancelled" },
    }),
    "completed",
  );
  assert.equal(
    getTurnFinishedStatus({
      ...message,
      additionalProperties: { type: "turn-finished", status: "failed" },
    }),
    null,
  );
});

test("turn position reads the server turnId and positive in-turn sequence", () => {
  const message = {
    messageId: "message-1",
    role: "system",
    author: "$agw",
    contents: [],
    additionalProperties: { type: TURN_START_MESSAGE_TYPE, turnId: "turn-1", turnSequence: 1 },
  };

  assert.equal(TURN_START_MESSAGE_TYPE, "agw-turn-start");
  assert.equal(isTurnStartMessage(message), true);
  assert.deepEqual(getTurnPosition(message), { turnId: "turn-1", turnSequence: 1 });
  assert.equal(getTurnPosition({ ...message, additionalProperties: { turnId: "turn-1" } }), null);
  assert.equal(
    getTurnPosition({ ...message, additionalProperties: { turnId: "turn-1", turnSequence: 0 } }),
    null,
  );
  assert.equal(
    getTurnPosition({ ...message, additionalProperties: { turnId: "", turnSequence: 2 } }),
    null,
  );
});

test("turn lifecycle messages expose their conversation and turn only when both are present", () => {
  const message = {
    messageId: "message-1",
    role: "system",
    author: "$agw",
    contents: [],
    additionalProperties: {
      type: TURN_FINISHED_MESSAGE_TYPE,
      status: "failed",
      conversationId: "conversation-1",
      turnId: "turn-1",
    },
  };

  assert.deepEqual(getTurnIdentity(message), {
    conversationId: "conversation-1",
    turnId: "turn-1",
  });
  assert.equal(
    getTurnIdentity({
      ...message,
      additionalProperties: { type: TURN_FINISHED_MESSAGE_TYPE, status: "interrupted" },
    }),
    null,
  );
  assert.equal(
    getTurnIdentity({
      ...message,
      additionalProperties: { ...message.additionalProperties, conversationId: "" },
    }),
    null,
  );
  assert.equal(isSupersededTurnMessage(message), false);
  assert.equal(
    isSupersededTurnMessage({
      ...message,
      additionalProperties: { ...message.additionalProperties, superseded: true },
    }),
    true,
  );
});

test("shared reconnect delays stop after ten attempts and add independent jitter", (t) => {
  const random = t.mock.method(Math, "random", () => 0);
  assert.deepEqual(
    [...executionReconnectDelaysMs],
    [1_000, 2_000, 3_000, 5_000, 8_000, 13_000, 21_000, 34_000, 55_000, 60_000],
  );
  for (const [index, baseMs] of executionReconnectDelaysMs.entries()) {
    random.mock.mockImplementation(() => 0);
    assert.equal(getExecutionReconnectDelay(index), baseMs);
    random.mock.mockImplementation(() => 0.5);
    assert.equal(getExecutionReconnectDelay(index), Math.round(baseMs * 1.1));
    random.mock.mockImplementation(() => 1 - Number.EPSILON);
    assert.equal(getExecutionReconnectDelay(index), Math.round(baseMs * 1.2));
  }
  assert.equal(random.mock.callCount(), 30);
  assert.equal(getExecutionReconnectDelay(executionReconnectDelaysMs.length), null);
  assert.equal(random.mock.callCount(), 30, "exhaustion does not sample jitter");
});
