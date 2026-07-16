import assert from "node:assert/strict";
import test from "node:test";

test("single-line reasoning uses a visibly shorter collapsed preview", async () => {
  const { getReasoningPreview } = await import("./reasoning" + ".ts");
  const content = "reasoning ".repeat(40).trim();

  const preview = getReasoningPreview(content);

  assert.ok(preview.length < content.length);
  assert.match(preview, /…$/);
});
