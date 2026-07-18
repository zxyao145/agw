import assert from "node:assert/strict";
import test from "node:test";

import { ErrorBoundary } from "./error-boundary";

test("ErrorBoundary resets a captured error when a reset key changes", () => {
  const boundary = new ErrorBoundary({ children: null, resetKeys: ["next"] });
  let resetCalled = false;
  boundary.state = { hasError: true, error: new Error("boom") };
  boundary.reset = () => {
    resetCalled = true;
  };

  boundary.componentDidUpdate({ children: null, resetKeys: ["previous"] });

  assert.equal(resetCalled, true);
});

test("ErrorBoundary keeps a captured error when reset keys are unchanged", () => {
  const boundary = new ErrorBoundary({ children: null, resetKeys: ["same"] });
  let resetCalled = false;
  boundary.state = { hasError: true, error: new Error("boom") };
  boundary.reset = () => {
    resetCalled = true;
  };

  boundary.componentDidUpdate({ children: null, resetKeys: ["same"] });

  assert.equal(resetCalled, false);
});
