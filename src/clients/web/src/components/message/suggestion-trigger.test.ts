import assert from "node:assert/strict";
import test from "node:test";

import { getTrailingSuggestionTrigger, replaceTrailingSuggestion } from "./suggestion-trigger.ts";

test("detects only trailing slash and file trigger fragments", () => {
  assert.deepEqual(getTrailingSuggestionTrigger("Please run /dep"), {
    type: "command",
    query: "dep",
    start: 11,
  });
  assert.deepEqual(getTrailingSuggestionTrigger("Open @src/app"), {
    type: "file",
    query: "src/app",
    start: 5,
  });
  assert.equal(getTrailingSuggestionTrigger("/deploy later"), null);
  assert.equal(getTrailingSuggestionTrigger("user@example.com"), null);
});

test("selection replaces only the trailing trigger and preserves preceding text", () => {
  assert.equal(replaceTrailingSuggestion("Please run /dep", "/deploy"), "Please run /deploy ");
  assert.equal(replaceTrailingSuggestion("Open @src/a", "@src/app.ts"), "Open @src/app.ts ");
});
