import assert from "node:assert/strict";
import test from "node:test";

import {
  getDeliveryStateLabel,
  getNextRunState,
  getScheduleError,
  isCronValue,
  isPositiveIsoDuration,
} from "./schedule-value.ts";

test("isPositiveIsoDuration accepts positive ISO 8601 durations", () => {
  for (const value of ["PT1M", "PT15M", "PT1H30M", "P1D", "P1DT2H", "P1M", "PT0.5S"]) {
    assert.equal(isPositiveIsoDuration(value), true, value);
  }
});

test("isPositiveIsoDuration rejects zero, .NET TimeSpan and malformed values", () => {
  for (const value of ["PT0S", "P0D", "00:15:00", "P", "PT", "15M", "", null]) {
    assert.equal(isPositiveIsoDuration(value), false, String(value));
  }
});

test("isPositiveIsoDuration validates the complete input", () => {
  assert.equal(isPositiveIsoDuration("xPT1M"), false);
  assert.equal(isPositiveIsoDuration("PT1Mx"), false);
});

test("isPositiveIsoDuration rejects forms unsupported by timer schedules", () => {
  for (const value of ["P1W", "PT1,5S", "PT1.5H", "P1DT"]) {
    assert.equal(isPositiveIsoDuration(value), false, value);
  }
});

test("isCronValue requires a standard five-field expression", () => {
  assert.equal(isCronValue("*/5 * * * *"), true);
  assert.equal(isCronValue("* * *"), false);
});

test("getScheduleError requires the field of the selected kind", () => {
  assert.equal(getScheduleError({ kind: "Once", at: "2026-08-01T09:00:00Z" }), null);
  assert.notEqual(getScheduleError({ kind: "Once" }), null);
  assert.notEqual(getScheduleError({ kind: "Interval", interval: "00:15:00" }), null);
  assert.notEqual(getScheduleError({ kind: "Cron", cron: "* * *" }), null);
});

test("getDeliveryStateLabel maps event delivery states", () => {
  assert.deepEqual([0, 1, 2, 3, 4, 5, 6, 7].map(getDeliveryStateLabel), [
    "Running",
    "Running",
    "Running",
    "Succeeded",
    "Retrying",
    "Running",
    "Failed",
    "Cancelled",
  ]);
});

test("getNextRunState ends only completed once jobs and hides disabled run times", () => {
  const dueAt = "2026-11-15T10:00:00Z";

  assert.equal(
    getNextRunState({
      enabled: true,
      scheduleCompleted: true,
      schedule: { kind: "Once" },
      dueAt: null,
    }),
    "ended",
  );
  assert.equal(
    getNextRunState({
      enabled: false,
      scheduleCompleted: false,
      schedule: { kind: "Once" },
      dueAt,
    }),
    "empty",
  );
  assert.equal(
    getNextRunState({
      enabled: true,
      scheduleCompleted: true,
      schedule: { kind: "Cron" },
      dueAt: null,
    }),
    "empty",
  );
  assert.equal(
    getNextRunState({
      enabled: true,
      scheduleCompleted: false,
      schedule: { kind: "Interval" },
      dueAt,
    }),
    "scheduled",
  );
});
