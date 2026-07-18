import assert from "node:assert/strict";
import test from "node:test";

import { aggregateExecutionStatus } from "./execution";

test("aggregateExecutionStatus follows the shared project priority", () => {
  assert.equal(aggregateExecutionStatus(["idle", "running"]), "running");
  assert.equal(aggregateExecutionStatus(["running", "failed-unread"]), "failed-unread");
  assert.equal(aggregateExecutionStatus(["failed-unread", "waiting-approval"]), "waiting-approval");
  assert.equal(aggregateExecutionStatus([]), "idle");
});
