import assert from "node:assert/strict";
import { readFileSync } from "node:fs";
import test from "node:test";

const source = readFileSync(new URL("./user-input.tsx", import.meta.url), "utf8");

test("suggestion kind badge keeps its content height inside the flex row", () => {
  assert.match(source, /<Badge className="[^"]*\bh-fit\b[^"]*\bself-start\b[^"]*">/);
});
