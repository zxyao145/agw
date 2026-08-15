import assert from "node:assert/strict";
import test from "node:test";
import { formatToolJson } from "./tool-json";

test("formats JSON array tool results and decodes unicode escapes", () => {
  const result = formatToolJson(
    String.raw`[{"name":"language","updatedAt":"2026-08-15T05:26:35.644893\u002B00:00"}]`,
  );

  assert.match(result, /"updatedAt": "2026-08-15T05:26:35\.644893\+00:00"/);
  assert.doesNotMatch(result, /\\u002B/);
  assert.match(result, /^\n```json\n\[/);
});

test("keeps non-JSON tool results unchanged", () => {
  assert.equal(formatToolJson("User memory written."), "User memory written.");
});
