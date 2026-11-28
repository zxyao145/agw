import assert from "node:assert/strict";
import test from "node:test";
import type { AiMessage } from "@agw/api";
import type { AgentflowNodeStatusEvent } from "@agw/execution-core";
import {
  applyAgentflowNodeMessage,
  applyAgentflowNodeStatus,
  EMPTY_AGENTFLOW_NODE_RUN_STATE,
  finishAgentflowNodeRun,
  getAgentflowNodeStatus,
} from "./agentflow-node-run-state";

const empty = EMPTY_AGENTFLOW_NODE_RUN_STATE;

function message(additionalProperties: Record<string, unknown>): AiMessage {
  return { messageId: "message", contents: [], additionalProperties };
}

function start(agentType = "agentflow", superseded = false): AiMessage {
  return message({
    type: "agw-turn-start",
    conversationId: "conversation-1",
    turnId: "turn-1",
    agentId: "flow-1",
    agentType,
    agentflowRevision: 3,
    superseded,
  });
}

function status(
  status: AgentflowNodeStatusEvent["status"],
  nodeId: string,
  interactionId?: string,
): AgentflowNodeStatusEvent {
  return {
    type: "agentflow-node-status",
    status,
    nodeId,
    ...(interactionId === undefined ? {} : { interactionId }),
  };
}

function snapshot(statuses: AgentflowNodeStatusEvent[], turnId = "turn-2") {
  return {
    type: "agentflow-node-snapshot" as const,
    turnId,
    agentflowId: "flow-2",
    agentflowRevision: 4,
    statuses,
  };
}

test("an Agentflow turn start resets the state to that turn", () => {
  const previous = applyAgentflowNodeStatus(empty, snapshot([status("running", "a")]));
  assert.deepEqual(applyAgentflowNodeMessage(previous, start()), {
    ...empty,
    turnId: "turn-1",
    agentflowId: "flow-1",
    agentflowRevision: 3,
    turnActive: true,
  });
});

test("an Agent turn start keeps only the turn id", () => {
  const previous = applyAgentflowNodeMessage(empty, start());
  assert.deepEqual(applyAgentflowNodeMessage(previous, start("agent")), {
    ...empty,
    turnId: "turn-1",
  });
});

test("running then completed ends as completed", () => {
  const state = [status("running", "agent"), status("completed", "agent")].reduce(
    applyAgentflowNodeStatus,
    empty,
  );
  assert.equal(getAgentflowNodeStatus(state, "agent"), "completed");
});

test("a completed node that runs again shows running", () => {
  const state = [status("completed", "agent"), status("running", "agent")].reduce(
    applyAgentflowNodeStatus,
    empty,
  );
  assert.equal(getAgentflowNodeStatus(state, "agent"), "running");
});

test("waiting lasts until its own interaction is answered", () => {
  const waiting = [
    status("waiting", "gate", "i1"),
    status("waiting", "gate", "i2"),
    status("running", "gate", "i1"),
  ].reduce(applyAgentflowNodeStatus, empty);
  assert.equal(getAgentflowNodeStatus(waiting, "gate"), "waiting");
  assert.deepEqual(waiting.pendingInteractionIdsByNodeId, { gate: ["i2"] });

  const answered = applyAgentflowNodeStatus(waiting, status("completed", "gate", "i2"));
  assert.equal(getAgentflowNodeStatus(answered, "gate"), "completed");
  assert.deepEqual(answered.pendingInteractionIdsByNodeId, {});
});

test("a snapshot of a new turn replaces the state", () => {
  const previous = applyAgentflowNodeStatus(
    applyAgentflowNodeMessage(empty, start()),
    status("running", "a"),
  );
  assert.deepEqual(applyAgentflowNodeStatus(previous, snapshot([status("running", "b")])), {
    ...empty,
    turnId: "turn-2",
    agentflowId: "flow-2",
    agentflowRevision: 4,
    turnActive: true,
    statusByNodeId: { b: "running" },
  });
});

test("a snapshot followed by the replayed suffix equals folding every event", () => {
  const events = [
    status("running", "a"),
    status("waiting", "a", "i2"),
    status("waiting", "a", "i1"),
    status("running", "a", "i1"),
    status("completed", "b"),
  ];
  const full = events.reduce(applyAgentflowNodeStatus, empty);
  const prefix = applyAgentflowNodeStatus(empty, snapshot(events.slice(0, 4)));
  const replay = events.slice(2).reduce(applyAgentflowNodeStatus, prefix);
  assert.deepEqual(replay.statusByNodeId, full.statusByNodeId);
  assert.deepEqual(replay.pendingInteractionIdsByNodeId, full.pendingInteractionIdsByNodeId);
});

test("a finish of another turn leaves the state unchanged", () => {
  const state = applyAgentflowNodeMessage(empty, start());
  const finished = message({ type: "agw-turn-finished", turnId: "turn-2" });
  assert.equal(applyAgentflowNodeMessage(state, finished), state);
});

test("a finish removes running and pending and keeps completed and failed", () => {
  const started = applyAgentflowNodeMessage(empty, start());
  const state = [
    status("running", "a"),
    status("completed", "b"),
    status("failed", "c"),
    status("waiting", "gate", "i1"),
  ].reduce(applyAgentflowNodeStatus, started);
  const withMember = applyAgentflowNodeMessage(
    state,
    message({ nodeId: "a", nodePath: "a/a.one" }),
  );
  const finished = applyAgentflowNodeMessage(
    withMember,
    message({
      type: "agw-turn-finished",
      turnId: "turn-1",
      conversationId: "conversation-1",
      status: "interrupted",
    }),
  );
  assert.deepEqual(finished, {
    ...started,
    turnActive: false,
    statusByNodeId: { b: "completed", c: "failed" },
  });
});

test("a superseded turn start leaves the state unchanged", () => {
  const state = applyAgentflowNodeStatus(empty, snapshot([status("running", "a")]));
  assert.equal(applyAgentflowNodeMessage(state, start("agentflow", true)), state);
});

test("member output marks the active member", () => {
  const output = message({ nodeId: "block", nodePath: "block/block.writer.manager" });
  assert.deepEqual(applyAgentflowNodeMessage(empty, output).activeMemberIdByNodeId, {
    block: "writer",
  });
});

test("unchanged inputs return the same object", () => {
  const completed = status("completed", "a");
  const waiting = status("waiting", "gate", "i1");
  const event = snapshot([status("running", "a"), completed, waiting]);
  const state = applyAgentflowNodeStatus(empty, event);
  assert.equal(applyAgentflowNodeStatus(state, event), state);
  assert.equal(applyAgentflowNodeStatus(state, completed), state);
  assert.equal(applyAgentflowNodeStatus(state, waiting), state);
  assert.equal(applyAgentflowNodeStatus(state, status("waiting", "a")), state);
  assert.equal(applyAgentflowNodeMessage(state, message({})), state);
  assert.equal(
    applyAgentflowNodeStatus(state, {
      type: "permission-status",
      activePermissionMode: "alwaysAsk",
      nextPermissionMode: "fullAccess",
      permissionChangePending: true,
    }),
    state,
  );
  const output = message({ nodeId: "block", nodePath: "block/block.writer" });
  const withMember = applyAgentflowNodeMessage(state, output);
  assert.equal(applyAgentflowNodeMessage(withMember, { ...output, messageId: "next" }), withMember);
  assert.equal(finishAgentflowNodeRun(empty), empty);
});
