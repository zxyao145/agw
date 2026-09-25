import assert from "node:assert/strict";
import test from "node:test";

import { getTriggerValueError, TRIGGER_TYPE_CRON, TRIGGER_TYPE_INTERVAL } from "./trigger-value.ts";

test("valid interval and cron values have no trigger value error", () => {
  assert.equal(getTriggerValueError(TRIGGER_TYPE_INTERVAL, "00:15:00"), null);
  assert.equal(getTriggerValueError(TRIGGER_TYPE_INTERVAL, "1.00:00:00"), null);
  assert.equal(getTriggerValueError(TRIGGER_TYPE_CRON, "0 1 * * *"), null);
  assert.equal(getTriggerValueError(TRIGGER_TYPE_CRON, "  */5 * * * *  "), null);
});

test("malformed interval values report an error instead of falling back", () => {
  for (const value of ["", "15m", "00:00:00", "*/1 * * * *"]) {
    assert.equal(
      getTriggerValueError(TRIGGER_TYPE_INTERVAL, value),
      "Enter a positive .NET TimeSpan value such as 00:01:00.",
    );
  }
});

test("cron values without five fields report an error instead of falling back", () => {
  for (const value of ["", "*/5 * * *", "0 0 1 * * *", "00:01:00"]) {
    assert.equal(
      getTriggerValueError(TRIGGER_TYPE_CRON, value),
      "Enter a standard five-field cron string such as */5 * * * *.",
    );
  }
});
