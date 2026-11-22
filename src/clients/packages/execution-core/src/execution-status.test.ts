import assert from "node:assert/strict";
import test from "node:test";
import type { ExecutionMessage } from "@agw/execution-types";
import { getExecutionStatus } from "./execution-status";

const message: ExecutionMessage = { messageId: "message", contents: [] };

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
