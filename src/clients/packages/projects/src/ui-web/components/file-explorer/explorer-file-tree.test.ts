import assert from "node:assert/strict";
import { readFile } from "node:fs/promises";
import test from "node:test";

const EXPLORER_FILE_TREE_URL = new URL("./explorer-file-tree.tsx", import.meta.url);
const EXPLORER_URL = new URL("./explorer.tsx", import.meta.url);

test("file explorer uses a shadcn alert dialog for delete confirmation", async () => {
  const source = await readFile(EXPLORER_FILE_TREE_URL, "utf8");

  assert.doesNotMatch(source, /\b(?:window\.)?confirm\s*\(/);
  assert.match(source, /<AlertDialog open=\{isDeleteDialogOpen\}/);
  assert.match(source, /<AlertDialogCancel[^>]*>Cancel<\/AlertDialogCancel>/);
  assert.match(source, /<AlertDialogAction[\s\S]*?variant="destructive"[\s\S]*?>/);
  assert.match(source, /item\.type === FileItemType\.Directory[\s\S]*?all its contents/);
});

test("file delete confirmation does not retain the context menu pointer lock", async () => {
  const source = await readFile(EXPLORER_FILE_TREE_URL, "utf8");

  assert.match(source, /<ContextMenu modal=\{false\}>/);
});

test("git change tree exposes hover controls for staging files and directories", async () => {
  const source = await readFile(EXPLORER_FILE_TREE_URL, "utf8");
  const scopeChangeStart = source.indexOf("const handleGitScopeChange");
  const scopeChangeEnd = source.indexOf("const FileIcon", scopeChangeStart);
  const scopeChangeSource = source.slice(scopeChangeStart, scopeChangeEnd);

  assert.match(
    source,
    /const targetScope: GitDiffScope = item\.gitScope === "staged" \? "unstaged" : "staged"/,
  );
  assert.match(source, /setFileStaged\(projectId, item\.path, targetScope === "staged"\)/);
  assert.match(
    source,
    /group-hover:opacity-100 group-focus-within:opacity-100 focus-visible:opacity-100/,
  );
  assert.match(source, /item\.gitScope === "staged" \? \([\s\S]*?<Minus[\s\S]*?: \([\s\S]*?<Plus/);
  assert.match(
    source,
    /aria-label=\{`\$\{item\.gitScope === "staged" \? "Unstage" : "Stage"\} \$\{item\.type\}/,
  );
  assert.match(source, /onGitScopeChanged\?\.\(item\.path, targetScope\)/);
  assert.doesNotMatch(scopeChangeSource, /toast\.success/);
});

test("git scope changes debounce the expensive root directory refresh", async () => {
  const source = await readFile(EXPLORER_URL, "utf8");
  const scopeChangeStart = source.indexOf("const handleGitScopeChanged");
  const scopeChangeEnd = source.indexOf("return (", scopeChangeStart);
  const scopeChangeSource = source.slice(scopeChangeStart, scopeChangeEnd);

  assert.match(source, /const ROOT_RELOAD_DEBOUNCE_MS = 150/);
  assert.match(source, /const scheduleRootDirectoryReload = React\.useCallback/);
  assert.match(source, /clearTimeout\(reloadTimeoutRef\.current\)/);
  assert.match(scopeChangeSource, /scheduleRootDirectoryReload\(\)/);
  assert.doesNotMatch(scopeChangeSource, /loadRootDirectory\(\)/);
});
