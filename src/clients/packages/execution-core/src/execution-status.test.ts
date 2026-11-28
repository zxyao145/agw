import assert from "node:assert/strict";
import test from "node:test";
import type { ExecutionMessage } from "@agw/execution-types";
import { getExecutionStatus, parseAgentflowNodeSnapshot } from "./execution-status";

const message: ExecutionMessage = { messageId: "message", contents: [] };

const snapshot = {
  turnId: "turn-1",
  agentflowId: "flow-1",
  agentflowRevision: 3,
  statuses: [
    { nodeId: "agent", status: "running", interactionId: null },
    { nodeId: "gate", status: "waiting", interactionId: "approval" },
  ],
};

test("parseAgentflowNodeSnapshot accepts a snapshot", () => {
  assert.deepEqual(parseAgentflowNodeSnapshot(snapshot), {
    type: "agentflow-node-snapshot",
    ...snapshot,
    statuses: [
      { type: "agentflow-node-status", nodeId: "agent", status: "running" },
      {
        type: "agentflow-node-status",
        nodeId: "gate",
        status: "waiting",
        interactionId: "approval",
      },
    ],
  });
});

test("parseAgentflowNodeSnapshot rejects malformed snapshots", () => {
  for (const invalid of [
    null,
    { ...snapshot, turnId: undefined },
    { ...snapshot, agentflowId: "" },
    { ...snapshot, agentflowRevision: 0 },
    { ...snapshot, statuses: "running" },
    { ...snapshot, statuses: [null] },
    { ...snapshot, statuses: [{ nodeId: "", status: "running" }] },
    { ...snapshot, statuses: [{ nodeId: "gate", status: "paused" }] },
    { ...snapshot, statuses: [{ nodeId: "gate", status: "waiting", interactionId: 1 }] },
  ]) {
    assert.throws(() => parseAgentflowNodeSnapshot(invalid), TypeError);
  }
});

test("getExecutionStatus reads node statuses and their interaction IDs", () => {
  const status = { type: "agentflow-node-status", nodeId: "gate", status: "waiting" };
  for (const properties of [status, { ...status, interactionId: "approval" }]) {
    assert.deepEqual(
      getExecutionStatus({ ...message, additionalProperties: properties }),
      properties,
    );
  }
});

test("getExecutionStatus rejects an invalid status message", () => {
  assert.throws(
    () =>
      getExecutionStatus({
        ...message,
        additionalProperties: { type: "agentflow-node-status", nodeId: "gate", status: "unknown" },
      }),
    /Invalid agentflow-node-status message/,
  );
});

test("getExecutionStatus reads mode and permission changes", () => {
  for (const properties of [
    {
      type: "permission-status",
      activePermissionMode: "alwaysAsk",
      nextPermissionMode: "fullAccess",
      permissionChangePending: true,
    },
    { type: "mode-status", agentId: "agent", mode: "plan" },
  ]) {
    assert.deepEqual(
      getExecutionStatus({ ...message, additionalProperties: properties }),
      properties,
    );
  }
  assert.deepEqual(
    getExecutionStatus({
      ...message,
      contents: [{ type: "TextContent", content: "Mode change failed." }],
      additionalProperties: { type: "mode-change-failed", agentId: "agent", mode: "plan" },
    }),
    { type: "mode-change-failed", agentId: "agent", mode: "plan", message: "Mode change failed." },
  );
});

test("getExecutionStatus leaves conversation messages in the message stream", () => {
  assert.equal(getExecutionStatus(message), null);
  for (const type of ["agw-turn-start", "tool-mode-status"]) {
    assert.equal(getExecutionStatus({ ...message, additionalProperties: { type } }), null);
  }
});
