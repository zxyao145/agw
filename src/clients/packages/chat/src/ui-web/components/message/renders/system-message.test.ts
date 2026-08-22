import assert from "node:assert/strict";
import { readFileSync } from "node:fs";
import test from "node:test";

const systemMessageSource = readFileSync(new URL("./system-message.tsx", import.meta.url), "utf8");

test("system message content can shrink within the message width", () => {
  const shrinkableContentColumns =
    systemMessageSource.match(/className="flex min-w-0 flex-1 flex-col(?: text-xs)?"/g) ?? [];

  assert.equal(shrinkableContentColumns.length, 2);
});
