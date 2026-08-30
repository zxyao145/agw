import assert from "node:assert/strict";
import test from "node:test";

import {
  createRendererEventRecord,
  RendererRecoveryGuard,
  sanitizeRendererReason,
} from "./renderer-recovery";

test("renderer recovery auto reloads once and requires manual recovery before stability", () => {
  const scheduled: Array<() => void> = [];
  const guard = new RendererRecoveryGuard(
    60_000,
    (callback, delayMs) => {
      assert.equal(delayMs, 60_000);
      scheduled.push(callback);
      return callback;
    },
    () => undefined,
  );

  assert.equal(guard.recordFailure(), "auto-reload");
  guard.markLoadSucceeded();
  assert.equal(guard.recordFailure(), "manual-recovery");

  guard.markLoadStarted();
  guard.markLoadSucceeded();
  assert.equal(guard.recordFailure(), "manual-recovery");
  guard.markLoadSucceeded();
  scheduled.at(-1)?.();

  assert.equal(guard.canAutomaticallyRecover(), true);
  assert.equal(guard.recordFailure(), "auto-reload");
});

test("renderer event records remove query parameters and credential-like failure details", () => {
  const event = createRendererEventRecord(
    {
      event: "did-fail-load",
      reason: "ERR_FAILED while loading ?token=secret-value",
      exitCode: -2,
      pathname:
        "agw://app/desktop/chat/?projectId=project-1&conversationId=conversation-1&token=secret-value",
    },
    {
      appVersion: "1.2.3",
      electronVersion: "43.4.0",
      os: "darwin 25.5.0",
      now: new Date("2026-08-13T01:02:03.000Z"),
    },
  );
  const serialized = JSON.stringify(event);

  assert.equal(event.pathname, "/desktop/chat/");
  assert.equal(event.reason, "ERR_FAILED");
  assert.doesNotMatch(serialized, /projectId|contextId|secret-value|token/);
  assert.equal(sanitizeRendererReason("a message with user content"), "unknown");
});
