import assert from "node:assert/strict";
import test from "node:test";

import {
  getTurnNotificationText,
  MAX_TURN_NOTIFICATION_TITLE_LENGTH,
  normalizeTurnNotificationRequest,
  normalizeTurnNotificationStatus,
} from "./turn-notification";

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

test("turn notification request keeps a sanitized conversation title", () => {
  assert.deepEqual(
    normalizeTurnNotificationRequest({ status: "completed", title: "  Refactor  " }),
    {
      status: "completed",
      title: "Refactor",
    },
  );
  assert.deepEqual(
    normalizeTurnNotificationRequest({ status: "failed", title: "line1\nline2\ttab" }),
    { status: "failed", title: "line1 line2 tab" },
  );
  assert.deepEqual(normalizeTurnNotificationRequest({ status: "completed" }), {
    status: "completed",
    title: undefined,
  });
  assert.deepEqual(normalizeTurnNotificationRequest({ status: "completed", title: "   " }), {
    status: "completed",
    title: undefined,
  });
  assert.deepEqual(normalizeTurnNotificationRequest({ status: "completed", title: 42 }), {
    status: "completed",
    title: undefined,
  });
  assert.equal(normalizeTurnNotificationRequest({ status: "interrupted" }), null);
  assert.equal(normalizeTurnNotificationRequest("completed"), null);
});

test("turn notification title is truncated by code points with an ellipsis", () => {
  const longTitle = "会".repeat(MAX_TURN_NOTIFICATION_TITLE_LENGTH + 10);
  const normalized = normalizeTurnNotificationRequest({ status: "completed", title: longTitle });
  const title = Array.from(normalized?.title ?? "");
  assert.equal(title.length, MAX_TURN_NOTIFICATION_TITLE_LENGTH);
  assert.equal(title[title.length - 1], "…");

  const surrogate = normalizeTurnNotificationRequest({
    status: "completed",
    title: "🦄".repeat(MAX_TURN_NOTIFICATION_TITLE_LENGTH),
  });
  assert.equal(Array.from(surrogate?.title ?? "").length, MAX_TURN_NOTIFICATION_TITLE_LENGTH);
});

test("turn notification text uses the conversation title as headline when present", () => {
  assert.deepEqual(getTurnNotificationText("completed", "Refactor auth"), {
    title: "Refactor auth",
    body: "A running task in Agw Desktop has finished.",
  });
  assert.deepEqual(getTurnNotificationText("failed", "Refactor auth"), {
    title: "Refactor auth",
    body: "A running task in Agw Desktop has failed.",
  });
});
