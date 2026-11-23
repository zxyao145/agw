import assert from "node:assert/strict";
import test from "node:test";

import { activeHeadingIndex, outlineLabel, sameOutline } from "./outline";

test("outlineLabel collapses runs of whitespace, newlines and tabs and trims", () => {
  assert.equal(outlineLabel("  Install\n\tthe   tool  "), "Install the tool");
  assert.equal(outlineLabel(" \n\t "), "");
});

test("sameOutline compares the level and label of every item", () => {
  const outline = [
    { level: 2, label: "A" },
    { level: 3, label: "B" },
  ] as const;
  assert.equal(
    sameOutline(outline, [
      { level: 2, label: "A" },
      { level: 3, label: "B" },
    ]),
    true,
  );
  assert.equal(
    sameOutline(outline, [
      { level: 2, label: "A" },
      { level: 4, label: "B" },
    ]),
    false,
  );
  assert.equal(
    sameOutline(outline, [
      { level: 2, label: "A" },
      { level: 3, label: "C" },
    ]),
    false,
  );
  assert.equal(sameOutline(outline, [{ level: 2, label: "A" }]), false);
});

test("activeHeadingIndex is null without headings and before the first heading", () => {
  assert.equal(activeHeadingIndex([], { height: 600, atBottom: false }), null);
  assert.equal(activeHeadingIndex([25, 300], { height: 600, atBottom: false }), null);
});

test("activeHeadingIndex returns the last heading at or above 24px", () => {
  assert.equal(activeHeadingIndex([-400, -10, 24, 25], { height: 600, atBottom: false }), 2);
  assert.equal(activeHeadingIndex([-400, 100], { height: 600, atBottom: false }), 0);
});

test("at the bottom activeHeadingIndex returns the last heading whose top is inside the visible part", () => {
  assert.equal(activeHeadingIndex([-400, 300, 599, 600], { height: 600, atBottom: true }), 2);
  assert.equal(activeHeadingIndex([700], { height: 600, atBottom: true }), null);
});
