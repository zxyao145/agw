import assert from "node:assert/strict";
import test from "node:test";

import { normalizeFloatingChatContext } from "./floating-chat";

test("normalizeFloatingChatContext copies string entries", () => {
  const source = { projectRelativePath: "plans/hello.md" };

  const result = normalizeFloatingChatContext(source);

  assert.deepEqual(result, source);
  assert.notEqual(result, source);
});

test("normalizeFloatingChatContext preserves special object keys", () => {
  const result = normalizeFloatingChatContext(Object.fromEntries([["__proto__", "value"]]));

  assert.equal(result["__proto__"], "value");
});

test("normalizeFloatingChatContext rejects values that are not string maps", () => {
  assert.throws(() => normalizeFloatingChatContext(null), { code: "ERR_INVALID_ARGUMENT" });
  assert.throws(() => normalizeFloatingChatContext([]), { code: "ERR_INVALID_ARGUMENT" });
  assert.throws(() => normalizeFloatingChatContext({ lineNumber: 3 }), {
    code: "ERR_INVALID_ARGUMENT",
  });
});
