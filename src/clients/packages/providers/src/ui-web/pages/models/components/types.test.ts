import assert from "node:assert/strict";
import test from "node:test";

import { getModelTokenLimitError } from "./types";

test("getModelTokenLimitError accepts a positive output limit below the context window", () => {
  assert.equal(getModelTokenLimitError(256_000, 64_000), null);
});

test("getModelTokenLimitError rejects invalid token relationships", () => {
  assert.match(getModelTokenLimitError(0, 1)!, /Context window/);
  assert.match(getModelTokenLimitError(1, 0)!, /Maximum output/);
  assert.match(getModelTokenLimitError(1_000, 1_000)!, /smaller/);
  assert.match(getModelTokenLimitError(1_000, 2_000)!, /smaller/);
});
