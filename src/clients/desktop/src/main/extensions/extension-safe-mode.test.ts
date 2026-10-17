import assert from "node:assert/strict";
import test from "node:test";

import { ExtensionSafeModeGuard } from "./extension-safe-mode";

test("a host crash or hang during activation enters safe mode with every activating extension", () => {
  for (const reason of ["crashed", "unresponsive"] as const) {
    const guard = new ExtensionSafeModeGuard();
    guard.beginActivation("first");
    guard.beginActivation("second");
    assert.deepEqual(guard.recordHostFailure(reason), {
      reason,
      extensionIds: ["first", "second"],
    });
    assert.deepEqual(guard.state, { reason, extensionIds: ["first", "second"] });
  }
});

test("a crash after activation finished does not enter safe mode", () => {
  const guard = new ExtensionSafeModeGuard();
  guard.beginActivation("first");
  guard.endActivation("first");
  assert.equal(guard.recordHostFailure("crashed"), null);
  assert.equal(guard.state, null);
});

test("manual entry and resume", () => {
  const guard = new ExtensionSafeModeGuard();
  assert.deepEqual(guard.enterManual(), { reason: "manual", extensionIds: [] });
  guard.resume();
  assert.equal(guard.state, null);
});

test("clearing activations afterwards does not change the recorded state", () => {
  const guard = new ExtensionSafeModeGuard();
  guard.beginActivation("first");
  const state = guard.recordHostFailure("crashed");
  guard.clearActivations();
  assert.deepEqual(guard.activatingIds, []);
  assert.deepEqual(state, { reason: "crashed", extensionIds: ["first"] });
  assert.deepEqual(guard.state, { reason: "crashed", extensionIds: ["first"] });
});
