import assert from "node:assert/strict";
import test from "node:test";

import { cn } from "./cn.ts";

test("cn keeps the theme font sizes next to color classes", () => {
  assert.equal(cn("text-11", "text-muted-foreground"), "text-11 text-muted-foreground");
  assert.equal(cn("text-2xs", "text-primary"), "text-2xs text-primary");
  assert.equal(cn("text-15 text-foreground"), "text-15 text-foreground");
});

test("cn lets a later theme font size replace an earlier one", () => {
  assert.equal(cn("text-sm", "text-13"), "text-13");
  assert.equal(cn("text-3xs", "text-xs"), "text-xs");
});
