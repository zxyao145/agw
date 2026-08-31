import assert from "node:assert/strict";
import test from "node:test";

import { getTurnNotificationText, normalizeTurnNotificationStatus } from "./turn-notification";

test("turn notification accepts the whitelisted terminal statuses", () => {
  assert.equal(normalizeTurnNotificationStatus({ status: "completed" }), "completed");
  assert.equal(normalizeTurnNotificationStatus({ status: "failed" }), "failed");
});

test("turn notification rejects interrupted and arbitrary payloads", () => {
  assert.equal(normalizeTurnNotificationStatus({ status: "interrupted" }), null);
  assert.equal(normalizeTurnNotificationStatus({ status: "<script>" }), null);
  assert.equal(normalizeTurnNotificationStatus("completed"), null);
  assert.equal(normalizeTurnNotificationStatus(null), null);
  assert.equal(normalizeTurnNotificationStatus(undefined), null);
  assert.equal(normalizeTurnNotificationStatus({}), null);
});

test("turn notification text never includes conversation content", () => {
  assert.deepEqual(getTurnNotificationText("completed"), {
    title: "Turn completed",
    body: "A running task in Agw Desktop has finished.",
  });
  assert.deepEqual(getTurnNotificationText("failed"), {
    title: "Turn failed",
    body: "A running task in Agw Desktop has failed.",
  });
});
