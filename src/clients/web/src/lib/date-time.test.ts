import assert from "node:assert/strict";
import test from "node:test";

// @ts-expect-error Node's type stripping requires the explicit TypeScript extension.
import * as dateTime from "./date-time.ts";

const { formatLocalDateTime, parseApiDateTime } = dateTime;

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
    assert.equal(
      formatLocalDateTime("2026-01-02T03:04:05"),
      new Date("2026-01-02T03:04:05Z").toLocaleString(),
    );
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
