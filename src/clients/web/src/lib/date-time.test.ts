import assert from "node:assert/strict";
import test from "node:test";

// @ts-expect-error Node's type stripping requires the explicit TypeScript extension.
import * as dateTime from "./date-time.ts";

const { formatLocalDateTime, parseApiDateTime } = dateTime;
const { formatFriendlyLocalDateTime } = dateTime as typeof dateTime & {
  formatFriendlyLocalDateTime?: (value: string, now?: Date) => string;
};

test("API timestamps use UTC semantics and display in the runtime local time zone", () => {
  const previousTimeZone = process.env.TZ;
  process.env.TZ = "Asia/Singapore";

  try {
    const exactFormatter = (
      dateTime as typeof dateTime & {
        formatLocalDateTimeExact?: (date: Date) => string;
      }
    ).formatLocalDateTimeExact;

    assert.equal(exactFormatter?.(new Date("2026-01-02T03:04:05Z")), "2026-01-02 11:04:05");
    assert.equal(
      parseApiDateTime("2026-01-02T03:04:05Z")?.toISOString(),
      "2026-01-02T03:04:05.000Z",
    );
    assert.equal(
      parseApiDateTime("2026-01-02T03:04:05")?.toISOString(),
      "2026-01-02T03:04:05.000Z",
    );
    assert.equal(
      parseApiDateTime("2026-01-02T03:04:05+02:00")?.toISOString(),
      "2026-01-02T01:04:05.000Z",
    );
    assert.equal(formatLocalDateTime("2026-01-02T03:04:05"), "2026-01-02 11:04:05");
  } finally {
    if (previousTimeZone === undefined) {
      delete process.env.TZ;
    } else {
      process.env.TZ = previousTimeZone;
    }
  }
});

test("recent API timestamps use friendly display text before falling back to exact local time", () => {
  const previousTimeZone = process.env.TZ;
  process.env.TZ = "Asia/Singapore";

  try {
    assert.equal(typeof formatFriendlyLocalDateTime, "function");
    if (!formatFriendlyLocalDateTime) return;

    const now = new Date("2026-01-02T03:04:05Z");

    assert.equal(formatFriendlyLocalDateTime("2026-01-02T03:03:30Z", now), "Just now");
    assert.equal(formatFriendlyLocalDateTime("2026-01-02T02:34:05Z", now), "30m ago");
    assert.equal(formatFriendlyLocalDateTime("2026-01-01T05:04:05Z", now), "22h ago");
    assert.equal(formatFriendlyLocalDateTime("2026-01-01T03:04:05Z", now), "2026-01-01 11:04:05");
    assert.equal(formatFriendlyLocalDateTime("2026-01-02T03:05:05Z", now), "2026-01-02 11:05:05");
  } finally {
    if (previousTimeZone === undefined) {
      delete process.env.TZ;
    } else {
      process.env.TZ = previousTimeZone;
    }
  }
});

test("invalid and missing API timestamps use stable fallbacks", () => {
  assert.equal(parseApiDateTime("invalid"), null);
  assert.equal(formatLocalDateTime(null), "-");
  assert.equal(formatLocalDateTime("invalid"), "invalid");
});
