import assert from "node:assert/strict";
import test from "node:test";

import {
  createProblemsChecker,
  EMPTY_PROBLEMS_STATE,
  hasPassedValidation,
} from "./agentflow-problems";
import { createEmptyDocument } from "./agentflow-document";

function wait(ms: number): Promise<void> {
  return new Promise((resolve) => setTimeout(resolve, ms));
}

test("a newer request cancels the older one", async () => {
  const signals: AbortSignal[] = [];
  const states: Array<{ fingerprint: string | null; problems: unknown[] }> = [];
  const checker = createProblemsChecker({
    delayMs: 0,
    validate: async (_, signal) => {
      signals.push(signal);
      await wait(10);
      return [];
    },
    onChange: (state) => states.push({ fingerprint: state.fingerprint, problems: state.problems }),
  });

  checker.check({ agentflowId: null, document: createEmptyDocument(), fingerprint: "first" }, true);
  checker.check(
    { agentflowId: null, document: createEmptyDocument(), fingerprint: "second" },
    true,
  );
  await wait(20);
  const secondAborted = signals[1].aborted;
  checker.dispose();

  assert.equal(signals[0].aborted, true);
  assert.equal(secondAborted, false);
  assert.equal(states.at(-1)?.fingerprint, "second");
});

test("a stale result does not enable Save", () => {
  assert.equal(
    hasPassedValidation({ ...EMPTY_PROBLEMS_STATE, fingerprint: "old" }, "current"),
    false,
  );
});

test("a failed request reports the error and keeps prior problems", async () => {
  const previousProblem = {
    code: "missing",
    message: "Select an Agent",
    nodeId: "agent",
    edgeId: null,
  };
  const states: Array<{ status: string; error: string | null; problems: unknown[] }> = [];
  let callCount = 0;
  const checker = createProblemsChecker({
    validate: async () => {
      callCount += 1;
      if (callCount === 1) return [previousProblem];
      throw new Error("Connection failed");
    },
    onChange: (state) =>
      states.push({ status: state.status, error: state.error, problems: state.problems }),
  });
  checker.check(
    { agentflowId: null, document: createEmptyDocument(), fingerprint: "previous" },
    true,
  );
  await wait(0);
  checker.check(
    { agentflowId: null, document: createEmptyDocument(), fingerprint: "current" },
    true,
  );
  await wait(0);
  checker.dispose();

  assert.equal(states.at(-1)?.status, "failed");
  assert.equal(states.at(-1)?.error, "Connection failed");
  assert.deepEqual(states.at(-1)?.problems, [previousProblem]);
});

test("checks wait for the delay and the first check runs at once", async () => {
  let requestCount = 0;
  const checker = createProblemsChecker({
    delayMs: 20,
    validate: async () => {
      requestCount += 1;
      return [];
    },
    onChange: () => undefined,
  });

  checker.check(
    { agentflowId: null, document: createEmptyDocument(), fingerprint: "initial" },
    true,
  );
  await wait(0);
  assert.equal(requestCount, 1);
  checker.check({ agentflowId: null, document: createEmptyDocument(), fingerprint: "next" });
  await wait(5);
  assert.equal(requestCount, 1);
  await wait(25);
  assert.equal(requestCount, 2);
  checker.dispose();
});
