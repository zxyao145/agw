import assert from "node:assert/strict";
import test from "node:test";
import { mergeStreamingMessages } from "./message";
import type { ExecutionMessage } from "./types";

test("streaming timestamp stays stable, fills missing values and remains scoped to its turn", () => {
  const message = (scope: string, createdAt?: string): ExecutionMessage => ({
    messageId: "item_0",
    streamingScopeId: scope,
    role: "assistant",
    createdAt,
    contents: [{ type: "TextContent", content: "text" }],
  });
  const first = "2026-09-20T13:38:00Z";
  const later = "2026-09-20T13:40:00Z";
  const merged = mergeStreamingMessages(
    [],
    [message("one"), message("one", first), message("one", later), message("two", later)],
  );
  assert.equal(merged[0].createdAt, first);
  assert.equal(merged[1].createdAt, later);
  assert.equal(
    mergeStreamingMessages([message("one", "invalid")], [message("one", first)])[0].createdAt,
    first,
  );
});
