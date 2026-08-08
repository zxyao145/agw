import assert from "node:assert/strict";
import { readFileSync } from "node:fs";
import test from "node:test";

const source = readFileSync(new URL("./chat-input-toolbar.tsx", import.meta.url), "utf8");

test("Add menu groups Plan, Skills, and Tools", () => {
  assert.match(source, /aria-label="Add"/);
  assert.match(source, />Plan mode</);
  assert.match(source, /label="Skills"/);
  assert.match(source, /label="Tools"/);
  assert.match(source, /suggestion\.text === "\/mode_set"/);
  assert.match(source, /className="size-7[^"]*"[\s\S]*?aria-label="Add"/);
});

test("permission select exposes the three PermissionMode values", () => {
  assert.match(source, /fullAccess: "Full access"/);
  assert.match(source, /alwaysAsk: "Always ask"/);
  assert.match(source, /allowSameArguments: "Allow same arguments"/);
  assert.match(source, /permissionMode === "fullAccess"/);
  assert.match(source, /<SelectTrigger[\s\S]*?data-\[size=sm\]:h-7/);
  assert.match(source, /<Select[\s\S]*?disabled=\{isTransitioning\}/);
  assert.doesNotMatch(source, /<Select[\s\S]*?disabled=\{isExecuting \|\| isTransitioning\}/);
});

test("Plan status reveals an accessible close button on hover and focus", () => {
  assert.match(source, /<Separator[^>]*data-\[orientation=vertical\]:h-5/);
  assert.match(source, /aria-label="Turn plan mode off"/);
  assert.match(source, /onClick=\{\(\) => onAgentModeChange\("execute"\)\}/);
  assert.match(
    source,
    /<button[\s\S]*?disabled=\{isTransitioning\}[\s\S]*?aria-label="Turn plan mode off"/,
  );
  assert.match(source, /group-hover:opacity-0 group-focus-within:opacity-0/);
  assert.match(source, /group-hover:opacity-100 group-focus-within:opacity-100/);
  assert.match(source, /hover:bg-muted focus-within:bg-muted/);
});
