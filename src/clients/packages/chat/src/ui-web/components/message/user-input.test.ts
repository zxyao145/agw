import assert from "node:assert/strict";
import { readFileSync } from "node:fs";
import test from "node:test";

const source = readFileSync(new URL("./user-input.tsx", import.meta.url), "utf8");

test("suggestion kind badge keeps its content height inside the flex row", () => {
  assert.match(source, /<Badge className="[^"]*\bh-fit\b[^"]*\bself-start\b[^"]*">/);
});

test("composer defaults to one text line with a floating circular action", () => {
  assert.match(source, /rows = 1/);
  assert.match(source, /maxHeight = "max-h-80"/);
  assert.match(source, /\bagw-scrollbar min-h-\[1lh\]/);
  assert.doesNotMatch(source, /\bmin-h-28\b/);
  assert.match(source, /className="[^"]*\brounded-xl\b[^"]*"/);
  assert.match(source, /className="[^"]*\bbg-background\b[^"]*\bshadow-sm\b[^"]*"/);
  assert.doesNotMatch(source, /bg-backgroundshadow-sm/);
  assert.match(source, /\bagw-scrollbar\b/);
  assert.match(source, /className="absolute left-2 right-2 bottom-2 flex justify-between"/);
  assert.match(source, /size="icon-sm"[\s\S]*?className="rounded-full"/);
  assert.match(source, /<ArrowUp className="size-5" \/>/);
});
