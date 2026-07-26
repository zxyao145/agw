import assert from "node:assert/strict";
import { readFile } from "node:fs/promises";
import test from "node:test";

const PAGE_URL = new URL("./page.tsx", import.meta.url);

test("Create and Edit Skill dialogs keep actions visible while the form body scrolls", async () => {
  const source = await readFile(PAGE_URL, "utf8");

  assert.match(
    source,
    /<DialogContent size="2xl" className="flex max-h-\[90vh\] flex-col overflow-hidden">/,
  );
  assert.match(source, /<DialogHeader className="shrink-0">/);
  assert.match(
    source,
    /className="grid min-h-0 flex-1 grid-cols-1 gap-6 overflow-y-auto agw-scrollbar pr-1 sm:grid-cols-2"/,
  );
  assert.match(source, /<DialogFooter className="shrink-0">/);
});

test("Built-in class skills are labeled and cannot be edited or deleted", async () => {
  const source = await readFile(PAGE_URL, "utf8");

  assert.match(source, /skill\.isBuiltIn \? \(/);
  assert.match(source, />\s*Class-based\s*<\/Badge>/);
  assert.match(source, /Managed by Agw/);
  assert.match(source, /skill\.isBuiltIn[\s\S]*openEditDialog\(skill\)/);
});
