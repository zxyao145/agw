import assert from "node:assert/strict";
import test from "node:test";

import { copyCurrentUrlToClipboard } from "./share-url";

test("copyCurrentUrlToClipboard writes the current URL to the clipboard", async () => {
  const copiedValues: string[] = [];

  await copyCurrentUrlToClipboard(
    "https://example.test/chat?projectId=1#settings=abc",
    async (value) => {
      copiedValues.push(value);
    },
  );

  assert.deepEqual(copiedValues, ["https://example.test/chat?projectId=1#settings=abc"]);
});
