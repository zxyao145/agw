import assert from "node:assert/strict";
import { readFileSync } from "node:fs";
import test from "node:test";

const source = readFileSync(new URL("./human-gate-approval.tsx", import.meta.url), "utf8");

test("tool approval actions follow PermissionMode", () => {
  assert.match(source, /permissionMode === "fullAccess"[\s\S]*?return null/);
  assert.match(source, /permissionMode === "alwaysAsk"[\s\S]*?onApprove\("once"\)/);
  assert.match(
    source,
    /permissionMode === "allowSameArguments"[\s\S]*?onApprove\("always-arguments"\)/,
  );
});

test("human gate content expands without nested scrolling", () => {
  assert.doesNotMatch(source, /max-h-\[45vh\]|line-clamp-2|max-h-28|overflow-auto/);
  assert.match(source, /whitespace-pre-wrap break-words text-sm text-foreground/);
  assert.match(
    source,
    /whitespace-pre-wrap break-words rounded-md bg-muted\/60[\s\S]*?request\.inputPreview/,
  );
  assert.match(
    source,
    /whitespace-pre-wrap break-words rounded-md border bg-muted\/40[\s\S]*?request\.arguments/,
  );
});

test("input mode uses interrupt and submit action labels", () => {
  assert.match(source, /rejectLabel = expectsInput \? "Interrupt" : "Reject"/);
  assert.match(source, /approveLabel = expectsInput \? "Submit" : "Approve"/);
  assert.match(source, /onReject[\s\S]*?\{rejectLabel\}/);
  assert.match(source, /onApprove[\s\S]*?\{approveLabel\}/);
});
