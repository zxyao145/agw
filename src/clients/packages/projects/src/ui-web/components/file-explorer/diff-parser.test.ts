import assert from "node:assert/strict";
import test from "node:test";
import { parseUnifiedDiff } from "./diff-parser";

test("parseUnifiedDiff preserves hunk line numbers and aligns additions with deletions", () => {
  const diff = [
    "diff --git a/sample.ts b/sample.ts",
    "index 1111111..2222222 100644",
    "--- a/sample.ts",
    "+++ b/sample.ts",
    "@@ -8,4 +8,5 @@ export function sample() {",
    " unchanged before",
    "-old value",
    "+new value",
    " unchanged after",
    " final context",
    "+added line",
  ].join("\n");

  const parsed = parseUnifiedDiff(diff);

  assert.deepEqual(
    parsed.original.map((line) => [line.kind, line.lineNumber, line.content]),
    [
      ["hunk", undefined, "@@ -8,4 +8,5 @@ export function sample() {"],
      ["context", 8, "unchanged before"],
      ["deletion", 9, "old value"],
      ["context", 10, "unchanged after"],
      ["context", 11, "final context"],
      ["placeholder", undefined, ""],
    ],
  );
  assert.deepEqual(
    parsed.modified.map((line) => [line.kind, line.lineNumber, line.content]),
    [
      ["hunk", undefined, "@@ -8,4 +8,5 @@ export function sample() {"],
      ["context", 8, "unchanged before"],
      ["addition", 9, "new value"],
      ["context", 10, "unchanged after"],
      ["context", 11, "final context"],
      ["addition", 12, "added line"],
    ],
  );
});

test("parseUnifiedDiff pads the shorter side of a replacement block", () => {
  const diff = [
    "@@ -20,2 +20,1 @@",
    "-first removed line",
    "-second removed line",
    "+replacement line",
    "\\ No newline at end of file",
  ].join("\n");

  const parsed = parseUnifiedDiff(diff);

  assert.deepEqual(
    parsed.original.map((line) => [line.kind, line.lineNumber]),
    [
      ["hunk", undefined],
      ["deletion", 20],
      ["deletion", 21],
      ["placeholder", undefined],
    ],
  );
  assert.deepEqual(
    parsed.modified.map((line) => [line.kind, line.lineNumber]),
    [
      ["hunk", undefined],
      ["addition", 20],
      ["placeholder", undefined],
      ["annotation", undefined],
    ],
  );
});

test("parseUnifiedDiff resets hunk state between files", () => {
  const diff = [
    "diff --git a/first.ts b/first.ts",
    "--- a/first.ts",
    "+++ b/first.ts",
    "@@ -1 +1 @@",
    "-first old",
    "+first new",
    "diff --git a/second.ts b/second.ts",
    "--- a/second.ts",
    "+++ b/second.ts",
    "@@ -5 +5 @@",
    "-second old",
    "+second new",
  ].join("\n");

  const parsed = parseUnifiedDiff(diff);

  assert.deepEqual(
    parsed.original.map((line) => line.content),
    ["@@ -1 +1 @@", "first old", "@@ -5 +5 @@", "second old"],
  );
  assert.deepEqual(
    parsed.modified.map((line) => line.content),
    ["@@ -1 +1 @@", "first new", "@@ -5 +5 @@", "second new"],
  );
});

test("parseUnifiedDiff removes carriage returns from CRLF content", () => {
  const parsed = parseUnifiedDiff(["@@ -1 +1 @@", "-old", "+new"].join("\r\n"));

  assert.equal(
    parsed.original.some((line) => line.content.endsWith("\r")),
    false,
  );
  assert.equal(
    parsed.modified.some((line) => line.content.endsWith("\r")),
    false,
  );
});

test("parseUnifiedDiff preserves no-newline annotations on the affected side", () => {
  const diff = ["@@ -1 +1 @@", "-old", "\\ No newline at end of file", "+new"].join("\n");

  const parsed = parseUnifiedDiff(diff);

  assert.deepEqual(
    parsed.original.map((line) => line.kind),
    ["hunk", "deletion", "annotation"],
  );
  assert.deepEqual(
    parsed.modified.map((line) => line.kind),
    ["hunk", "addition", "placeholder"],
  );
});
