import assert from "node:assert/strict";
import { readFile } from "node:fs/promises";
import test from "node:test";

const CONNECTION_DIALOG_URL = new URL("./connection-dialog.tsx", import.meta.url);

test("Connection dialog top-aligns identity fields with different content heights", async () => {
  const source = await readFile(CONNECTION_DIALOG_URL, "utf8");

  assert.match(
    source,
    /<div className="grid items-start gap-4 sm:grid-cols-2">[\s\S]*connection-display-name[\s\S]*connection-alias/,
  );
});

test("Connection dialog validates aliases with the server kebab-case contract", async () => {
  const source = await readFile(CONNECTION_DIALOG_URL, "utf8");

  assert.match(source, /isConnectionAliasValid\(editor\.alias\)/);
  assert.match(source, /placeholder="github-work"/);
  assert.match(source, /aria-invalid=\{aliasInvalid\}/);
  assert.match(source, /Use lowercase letters, numbers, and hyphens\./);
  assert.match(source, /disabled=\{[\s\S]*!aliasValid[\s\S]*\}/);
  assert.doesNotMatch(source, /github_work/);
});
