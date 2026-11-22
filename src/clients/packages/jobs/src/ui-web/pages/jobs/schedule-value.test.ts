import assert from "node:assert/strict";
import test from "node:test";

import {
  formatSavedJobInterval,
  getDeliveryStateLabel,
  getNextRunState,
  getScheduleError,
  isCronValue,
  isPositiveIsoDuration,
  normalizeJobIntervalInput,
} from "./schedule-value.ts";

test("normalizeJobIntervalInput returns the closest supported interval", () => {
  for (const [input, value, isoDuration, adjusted] of [
    ["00:00:00", "00:00:30", "PT30S", true],
    ["00:00:29", "00:00:30", "PT30S", true],
    ["00:00:30", "00:00:30", "PT30S", false],
    ["00:00:44", "00:00:30", "PT30S", true],
    ["00:00:45", "00:01:00", "PT1M", true],
    ["00:59:59", "01:00:00", "PT1H", true],
    ["01:02:03", "01:02:00", "PT1H2M", true],
    ["01:02:30", "01:02:30", "PT1H2M30S", false],
    ["24:00:00", "24:00:00", "PT24H", false],
    ["100:00:00", "100:00:00", "PT100H", false],
  ] as const) {
    assert.deepEqual(normalizeJobIntervalInput(input), { value, isoDuration, adjusted }, input);
  }
});

test("normalizeJobIntervalInput rejects invalid hour, minute and second fields", () => {
  for (const input of ["", "1:00:30", "00:60:00", "00:00:60", "00:00:30.5", "PT1M", "-1:00:00"]) {
    assert.equal(normalizeJobIntervalInput(input), null, input);
  }
});

test("formatSavedJobInterval displays original durations without changing their precision", () => {
  for (const [interval, expected] of [
    ["PT45S", "00:00:45"],
    ["PT1H2M3S", "01:02:03"],
    ["P1D", "24:00:00"],
    ["P1DT2H", "26:00:00"],
    ["P1M", "720:00:00"],
    ["P12M", "8760:00:00"],
    ["PT0.5S", "PT0.5S"],
  ]) {
    assert.equal(formatSavedJobInterval(interval), expected, interval);
  }
});

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
