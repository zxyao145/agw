import assert from "node:assert/strict";
import test from "node:test";

import { isValidSourceSuffix } from "@agw/api";

import { getSourceNamePrefix, splitSourceName } from "./source-name";

test("source names use their kind and manager prefixes", () => {
  assert.equal(getSourceNamePrefix(1, "user"), "webhook.");
  assert.equal(getSourceNamePrefix(0, "job"), "agw.timer.");
  assert.equal(splitSourceName("webhook.demo-ticket", "webhook."), "demo-ticket");
});

test("source suffix uses lowercase identifiers", () => {
  assert.equal(isValidSourceSuffix("demo-ticket"), true);
  for (const value of ["Demo", "a..b", "-a", "a-", ""]) {
    assert.equal(isValidSourceSuffix(value), false);
  }
});
