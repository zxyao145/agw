import assert from "node:assert/strict";
import test from "node:test";
import { getExecutionReconnectProgress } from "./execution-session";

test("reconnect presentation hides the silent attempts without changing internal counts", () => {
  assert.equal(getExecutionReconnectProgress(null), null);
  for (let retryAttempt = 1; retryAttempt <= 10; retryAttempt += 1) {
    const state = { status: "reconnecting" as const, retryAttempt, retryDelayMs: 1_000 };
    assert.deepEqual(
      getExecutionReconnectProgress(state),
      retryAttempt <= 5 ? null : { attempt: retryAttempt - 5, total: 5 },
    );
    assert.equal(state.retryAttempt, retryAttempt);
  }
  assert.deepEqual(
    getExecutionReconnectProgress({ status: "failed", retryAttempt: 10, retryDelayMs: 0 }),
    { attempt: 5, total: 5 },
  );
  assert.equal(
    getExecutionReconnectProgress({ status: "reconnecting", retryAttempt: 1, retryDelayMs: 0 }),
    null,
  );
});
