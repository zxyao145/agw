import assert from "node:assert/strict";
import { readFile } from "node:fs/promises";
import test from "node:test";

import { validateMemoryForm } from "./user-memory-page";

const PAGE_URL = new URL("./user-memory-page.tsx", import.meta.url);

test("User Memory validates trimmed metadata and required Markdown content", () => {
  assert.deepEqual(
    validateMemoryForm({
      name: "  Preferences  ",
      description: "  Answer style  ",
      content: "# Concise\n",
    }),
    {
      name: "Preferences",
      description: "Answer style",
      content: "# Concise\n",
    },
  );

  assert.throws(
    () => validateMemoryForm({ name: " ", description: "", content: "content" }),
    /name is required/i,
  );
  assert.throws(
    () => validateMemoryForm({ name: "name", description: "", content: " \n " }),
    /content is required/i,
  );
  assert.throws(
    () =>
      validateMemoryForm({
        name: "name",
        description: "x".repeat(301),
        content: "content",
      }),
    /300 characters/i,
  );
});

test("User Memory pages summaries and loads content only for an open editor", async () => {
  const source = await readFile(PAGE_URL, "utf8");

  assert.match(source, /apiGet\("\/api\/user-memories\/paged"/);
  assert.match(source, /<PaginatedTable/);
  assert.match(source, /apiGet\("\/api\/user-memories\/detail"/);
  assert.match(source, /enabled: editOpen && Boolean\(editingMemory\)/);
  assert.doesNotMatch(
    source.match(/type UserMemorySummary = \{[\s\S]*?\n\};/)?.[0] ?? "",
    /content:/,
  );
});

test("User Memory uses explicit save, deletion confirmation, and safe GFM preview", async () => {
  const source = await readFile(PAGE_URL, "utf8");

  assert.match(source, /<TabsTrigger value="edit">Edit<\/TabsTrigger>/);
  assert.match(source, /<TabsTrigger value="preview">Preview<\/TabsTrigger>/);
  assert.match(source, /<ReactMarkdown remarkPlugins=\{\[remarkGfm\]\}>/);
  assert.match(source, /placeholder=\{`### What agents should remember/);
  assert.doesNotMatch(source, /rehypeRaw|rehypePlugins/);
  assert.match(source, /Save Memory/);
  assert.match(source, /Delete memory/);
  assert.doesNotMatch(source, /autoSave|autosave/i);
});
