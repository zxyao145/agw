import assert from "node:assert/strict";
import test from "node:test";

import { aggregateExecutionStatus, getExecutionKey } from "./execution-status";

test("execution key isolates server, project, and conversation", () => {
  assert.equal(
    getExecutionKey({ serverId: "local", projectId: "project-1", contextId: "context-1" }),
    "local:project-1:context-1",
  );
});

test("aggregate execution status follows the locked project tab priority", () => {
  assert.equal(aggregateExecutionStatus(["idle", "running"]), "running");
  assert.equal(aggregateExecutionStatus(["running", "failed-unread"]), "failed-unread");
  assert.equal(aggregateExecutionStatus(["failed-unread", "waiting-approval"]), "waiting-approval");
  assert.equal(aggregateExecutionStatus([]), "idle");
});
