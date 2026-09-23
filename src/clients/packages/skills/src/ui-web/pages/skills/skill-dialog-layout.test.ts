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
    /className="grid min-h-0 flex-1 grid-cols-1 gap-6 overflow-y-scroll \[scrollbar-gutter:stable\] agw-scrollbar pr-1 sm:grid-cols-2"/,
  );
  assert.match(source, /<DialogFooter className="shrink-0">/);
});

test("Built-in class skills are labeled and cannot be edited or deleted", async () => {
  const source = await readFile(PAGE_URL, "utf8");

  assert.match(source, /skill\.kind === "BuiltIn" \|\| skill\.isBuiltIn \? \(/);
  assert.match(source, />\s*Class-based\s*<\/Badge>/);
  assert.match(
    source,
    /skill\.kind === "BuiltIn" \|\| skill\.isBuiltIn[\s\S]*openEditDialog\(skill\)/,
  );
});

test("Skill form switches between Local archive and Remote URL inputs", async () => {
  const source = await readFile(PAGE_URL, "utf8");

  assert.match(source, /<SelectItem[^>]*value="Local">\s*Local\s*<\/SelectItem>/);
  assert.match(source, /<SelectItem[^>]*value="Remote">\s*Remote\s*<\/SelectItem>/);
  assert.match(source, /form\.kind === "Local" \? \(/);
  assert.match(source, /id=\{`\$\{mode\}-skill-archive`\}/);
  assert.match(source, /id=\{`\$\{mode\}-skill-remote-url`\}/);
  assert.match(source, /expects a zip archive containing one/);
  assert.match(source, /Name and description are synchronized from the remote response/);
});

test("Remote skills show their URL and remain deletable", async () => {
  const source = await readFile(PAGE_URL, "utf8");

  assert.match(source, /skill\.kind === "Remote" && skill\.remoteUrl/);
  assert.match(source, /href=\{skill\.remoteUrl\}/);
  assert.match(source, /deletingSkill\?\.kind === "Local"/);
});
