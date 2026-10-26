import assert from "node:assert/strict";
import test from "node:test";

import { fileBaseName, matchesFileNamePattern } from "./file-viewer-patterns";

test("patterns match base names case-insensitively with * and ?", () => {
  assert.equal(matchesFileNamePattern("*.md", "README.MD"), true);
  assert.equal(matchesFileNamePattern("*.md", "notes.markdown"), false);
  assert.equal(matchesFileNamePattern("README", "readme"), true);
  assert.equal(matchesFileNamePattern("README", "README.md"), false);
  assert.equal(matchesFileNamePattern("?.txt", "a.txt"), true);
  assert.equal(matchesFileNamePattern("?.txt", "ab.txt"), false);
  assert.equal(matchesFileNamePattern("*", "anything.bin"), true);
  assert.equal(matchesFileNamePattern("*.excalidraw", "diagram.excalidraw"), true);
});

test("regular expression characters in patterns match literally", () => {
  assert.equal(matchesFileNamePattern("a+b(1).md", "a+b(1).md"), true);
  assert.equal(matchesFileNamePattern("a+b(1).md", "aab1.md"), false);
  assert.equal(matchesFileNamePattern("[x].md", "[x].md"), true);
  assert.equal(matchesFileNamePattern("[x].md", "x.md"), false);
  assert.equal(matchesFileNamePattern("*.md", "a.mdx"), false);
});

test("base names drop directories", () => {
  assert.equal(fileBaseName("docs/guide/README.md"), "README.md");
  assert.equal(fileBaseName("README.md"), "README.md");
  assert.equal(fileBaseName("dir\\file.md"), "file.md");
});
