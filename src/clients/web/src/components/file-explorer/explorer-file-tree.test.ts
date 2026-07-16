import assert from "node:assert/strict";
import { readFile } from "node:fs/promises";
import test from "node:test";

const EXPLORER_FILE_TREE_URL = new URL("./explorer-file-tree.tsx", import.meta.url);

test("file explorer uses a shadcn alert dialog for delete confirmation", async () => {
  const source = await readFile(EXPLORER_FILE_TREE_URL, "utf8");

  assert.doesNotMatch(source, /\b(?:window\.)?confirm\s*\(/);
  assert.match(source, /<AlertDialog open=\{isDeleteDialogOpen\}/);
  assert.match(source, /<AlertDialogCancel[^>]*>Cancel<\/AlertDialogCancel>/);
  assert.match(source, /<AlertDialogAction[\s\S]*?variant="destructive"[\s\S]*?>/);
  assert.match(source, /item\.type === FileItemType\.Directory[\s\S]*?all its contents/);
});
