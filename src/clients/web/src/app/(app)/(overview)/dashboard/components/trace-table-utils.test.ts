import assert from "node:assert/strict";
import test from "node:test";

import {
  buildTraceQuery,
  EMPTY_TRACE_FILTERS,
  extractTraceInputText,
  formatTraceStartTime,
  getNodeKindLabel,
  getPaginationMeta,
  getTraceStatusLabel,
} from "./trace-table-utils.ts";

test("buildTraceQuery trims filters and converts local datetimes to UTC", () => {
  const query = buildTraceQuery(
    {
      projectId: " project-id ",
      contextId: " context-id ",
      agentflowId: " agentflow-id ",
      fromUtc: "2026-07-13T09:00",
      toUtc: "2026-07-13T10:00",
    },
    2,
    50,
  );

  assert.deepEqual(query, {
    projectId: "project-id",
    contextId: "context-id",
    agentflowId: "agentflow-id",
    fromUtc: new Date("2026-07-13T09:00").toISOString(),
    toUtc: new Date("2026-07-13T10:00").toISOString(),
    pageIndex: 2,
    pageSize: 50,
  });
});

test("buildTraceQuery omits empty filters", () => {
  assert.deepEqual(buildTraceQuery(EMPTY_TRACE_FILTERS, 1, 20), {
    pageIndex: 1,
    pageSize: 20,
  });
});

test("getPaginationMeta describes a populated middle page", () => {
  assert.deepEqual(getPaginationMeta(41, 2, 20), {
    start: 21,
    end: 40,
    totalPages: 3,
    canGoPrevious: true,
    canGoNext: true,
  });
});

test("getPaginationMeta describes an empty first page", () => {
  assert.deepEqual(getPaginationMeta(0, 1, 20), {
    start: 0,
    end: 0,
    totalPages: 1,
    canGoPrevious: false,
    canGoNext: false,
  });
});

test("trace enum labels remain readable", () => {
  assert.equal(getTraceStatusLabel(0), "Succeeded");
  assert.equal(getTraceStatusLabel(9), "Unknown (9)");
  assert.equal(getNodeKindLabel(3), "Human Gate");
  assert.equal(getNodeKindLabel(99), "Unknown (99)");
});

test("extractTraceInputText joins only non-empty contents text values", () => {
  const input = JSON.stringify([
    { contents: [{ text: "first" }, { value: "ignored" }] },
    { contents: [{ text: "second" }, { text: "  " }] },
  ]);

  assert.equal(extractTraceInputText(input), "first\nsecond");
});

test("extractTraceInputText returns a dash for malformed JSON", () => {
  assert.equal(extractTraceInputText("not-json"), "—");
});

test("extractTraceInputText returns a dash when contents have no text", () => {
  const input = JSON.stringify([{ contents: [{ value: "ignored" }] }]);

  assert.equal(extractTraceInputText(input), "—");
});

test("formatTraceStartTime uses the runtime local time zone and exact format", () => {
  const previousTimeZone = process.env.TZ;
  process.env.TZ = "Asia/Singapore";

  try {
    assert.equal(formatTraceStartTime("2026-01-02T03:04:05"), "2026-01-02 11:04:05");
    assert.equal(formatTraceStartTime("2026-01-02T03:04:05Z"), "2026-01-02 11:04:05");
    assert.equal(formatTraceStartTime("2026-01-02T03:04:05+02:00"), "2026-01-02 09:04:05");
  } finally {
    if (previousTimeZone === undefined) {
      delete process.env.TZ;
    } else {
      process.env.TZ = previousTimeZone;
    }
  }
});

test("formatTraceStartTime returns a dash for an invalid timestamp", () => {
  assert.equal(formatTraceStartTime("invalid"), "—");
});
