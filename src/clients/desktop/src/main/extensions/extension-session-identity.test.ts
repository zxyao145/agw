import assert from "node:assert/strict";
import test from "node:test";

import { extensionSessionId } from "./extension-session-identity";

test("extensionSessionId uses the stable extension instance as the session key", () => {
  const instanceId = "server-" + "a".repeat(64);
  assert.equal(extensionSessionId(instanceId), instanceId);
});

test("server releases use the same persistent extension session", () => {
  const instanceId = "server-" + "a".repeat(64);
  assert.equal(extensionSessionId(instanceId), instanceId);
});
