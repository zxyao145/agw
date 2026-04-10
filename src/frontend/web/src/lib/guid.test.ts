import assert from "node:assert/strict";
import test from "node:test";

import { EMPTY_GUID, isNonEmptyGuid } from "./guid";

test("isNonEmptyGuid returns true for a canonical non-empty guid", () => {
  assert.equal(isNonEmptyGuid("550e8400-e29b-41d4-a716-446655440000"), true);
});

test("isNonEmptyGuid returns false for the empty guid", () => {
  assert.equal(isNonEmptyGuid(EMPTY_GUID), false);
});

test("isNonEmptyGuid returns false for malformed values", () => {
  assert.equal(isNonEmptyGuid("not-a-guid"), false);
  assert.equal(isNonEmptyGuid("550e8400e29b41d4a716446655440000"), false);
  assert.equal(isNonEmptyGuid(""), false);
});
