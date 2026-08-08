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
