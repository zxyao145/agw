import assert from "node:assert/strict";
import { readFile } from "node:fs/promises";
import test from "node:test";

const COMMENT_SECTION_URL = new URL("./comment-section.tsx", import.meta.url);

test("comment input submits with Ctrl, Command, or Shift plus Enter", async () => {
  const source = await readFile(COMMENT_SECTION_URL, "utf8");

  assert.match(source, /e\.key === "Enter" && \(e\.ctrlKey \|\| e\.metaKey \|\| e\.shiftKey\)/);
  assert.match(source, /e\.preventDefault\(\)/);
  assert.match(source, /Ctrl\/Shift\+Enter to submit, Esc to cancel/);
});

test("comment delete buttons stay visible inside horizontally scrolling diff panes", async () => {
  const source = await readFile(COMMENT_SECTION_URL, "utf8");

  assert.match(source, /sticky right-3 z-10 shrink-0 self-start/);
});

test("long comment text wraps within its diff pane", async () => {
  const source = await readFile(COMMENT_SECTION_URL, "utf8");

  assert.match(source, /whitespace-pre-wrap wrap-anywhere/);
});
