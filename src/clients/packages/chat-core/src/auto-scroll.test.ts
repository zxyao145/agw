import assert from "node:assert/strict";
import test from "node:test";

import { createAutoScrollState, updateAutoScrollState } from "./auto-scroll";

test("auto scroll pauses upward and resumes at the bottom", () => {
  const paused = updateAutoScrollState(
    { shouldAutoScroll: true, scrollHeight: 1_000, scrollTop: 500 },
    { clientHeight: 500, scrollHeight: 1_020, scrollTop: 450 },
  );
  assert.equal(paused.shouldAutoScroll, false);

  const resumed = updateAutoScrollState(paused, {
    clientHeight: 500,
    scrollHeight: 1_020,
    scrollTop: 520,
  });
  assert.equal(resumed.shouldAutoScroll, true);
  assert.deepEqual(createAutoScrollState(), {
    shouldAutoScroll: true,
    scrollHeight: 0,
    scrollTop: 0,
  });
});
