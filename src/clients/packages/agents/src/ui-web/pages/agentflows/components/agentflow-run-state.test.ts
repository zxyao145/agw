import assert from "node:assert/strict";
import test from "node:test";

import type { AiMessage } from "@agw/api";
import type { ExecutionStatusEvent } from "@agw/execution-core";

import {
  applyAgentflowRunMessage,
  applyAgentflowRunStatus,
  EMPTY_RUN_STATE,
  getNodeRunStatus,
} from "./agentflow-run-state";

function status(
  nodeId: string,
  state: "running" | "waiting" | "completed" | "failed",
  interactionId?: string,
): ExecutionStatusEvent {
  return {
    type: "agentflow-node-status",
    nodeId,
    status: state,
    ...(interactionId ? { interactionId } : {}),
  };
}

function message(messageId: string, properties: Record<string, unknown>, content = ""): AiMessage {
  return {
    messageId,
    additionalProperties: properties,
    contents: [{ type: "TextContent", content }],
  };
}

const start = message("start", {
  type: "agw-turn-start",
  turnId: "turn-1",
  agentId: "flow-1",
  agentType: "agentflow",
  agentflowRevision: 1,
});

test("turn start clears the state", () => {
  const started = applyAgentflowRunMessage(
    {
      ...EMPTY_RUN_STATE,
      statusByNodeId: { agent: "completed" },
      latestOutputByNodeId: { agent: { messageId: "output", text: "response" } },
      nodeIdByMessageId: { output: "agent" },
    },
    start,
  );

  assert.deepEqual(started.statusByNodeId, {});
  assert.deepEqual(started.latestOutputByNodeId, {});
  assert.deepEqual(started.nodeIdByMessageId, {});
  assert.equal(started.turnActive, true);
  assert.equal(started.turnId, "turn-1");
  assert.equal(started.agentflowId, "flow-1");
  assert.equal(started.agentflowRevision, 1);
});

test("the last status of a node wins", () => {
  const state = applyAgentflowRunStatus(
    applyAgentflowRunStatus(EMPTY_RUN_STATE, status("agent", "running")),
    status("agent", "failed"),
  );

  assert.equal(getNodeRunStatus(state, "agent"), "failed");
});

test("waiting holds until a status with the same interactionId arrives", () => {
  const waiting = applyAgentflowRunStatus(
    EMPTY_RUN_STATE,
    status("gate", "waiting", "interaction-1"),
  );
  const anotherAnswer = applyAgentflowRunStatus(
    waiting,
    status("gate", "completed", "interaction-2"),
  );
  const answered = applyAgentflowRunStatus(
    anotherAnswer,
    status("gate", "completed", "interaction-1"),
  );

  assert.equal(getNodeRunStatus(anotherAnswer, "gate"), "waiting");
  assert.equal(getNodeRunStatus(answered, "gate"), "completed");
});

test("a late completed status does not hide waiting", () => {
  const waiting = applyAgentflowRunStatus(
    EMPTY_RUN_STATE,
    status("gate", "waiting", "interaction-1"),
  );
  const completed = applyAgentflowRunStatus(waiting, status("gate", "completed"));

  assert.equal(getNodeRunStatus(completed, "gate"), "waiting");
});

test("turn finish removes running and waiting", () => {
  let state = applyAgentflowRunStatus(
    applyAgentflowRunMessage(EMPTY_RUN_STATE, start),
    status("agent", "running"),
  );
  state = applyAgentflowRunStatus(state, status("gate", "waiting", "interaction-1"));
  state = applyAgentflowRunMessage(state, message("finish", { type: "agw-turn-finished" }));

  assert.equal(getNodeRunStatus(state, "agent"), null);
  assert.equal(getNodeRunStatus(state, "gate"), null);
  assert.equal(state.turnActive, false);
});

test("output is attributed by nodeId", () => {
  const state = applyAgentflowRunMessage(
    EMPTY_RUN_STATE,
    message("output-1", { nodeId: "agent", nodePath: "agent" }, "response"),
  );

  assert.equal(state.nodeIdByMessageId["output-1"], "agent");
  assert.deepEqual(state.latestOutputByNodeId.agent, { messageId: "output-1", text: "response" });
});

test("member output marks the active member", () => {
  let state = applyAgentflowRunMessage(
    EMPTY_RUN_STATE,
    message("member-1", { nodeId: "b", nodePath: "b/b.m1" }, "one"),
  );
  state = applyAgentflowRunMessage(
    state,
    message("member-2", { nodeId: "b", nodePath: "b/b.m1.manager" }, "two"),
  );

  assert.equal(state.activeMemberIdByNodeId.b, "m1");
});
