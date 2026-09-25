import assert from "node:assert/strict";
import test from "node:test";

import { DEFAULT_HUMAN_STEP_MODE, readHumanStepMode } from "./config-json.ts";

test("human step mode defaults to approval like the server when humanMode is unset", () => {
  assert.equal(DEFAULT_HUMAN_STEP_MODE, "approval");
  assert.equal(readHumanStepMode({}), "approval");
  assert.equal(readHumanStepMode({ humanMode: "" }), "approval");
  assert.equal(readHumanStepMode({ humanMode: "   " }), "approval");
  assert.equal(readHumanStepMode({ humanMode: 1 }), "approval");
});

test("human step mode keeps an explicitly configured value", () => {
  assert.equal(readHumanStepMode({ humanMode: "input" }), "input");
  assert.equal(readHumanStepMode({ humanMode: " approval " }), "approval");
});
