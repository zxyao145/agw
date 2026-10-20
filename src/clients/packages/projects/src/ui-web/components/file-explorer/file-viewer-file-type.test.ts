import assert from "node:assert/strict";
import test from "node:test";

import { fileViewerFileType } from "./file-viewer-file-type.ts";

test("fileViewerFileType_NameWithExtension_ReturnsLowerCasedWildcardExtension", () => {
  assert.equal(fileViewerFileType("README.MD"), "*.md");
  assert.equal(fileViewerFileType("a.tar.gz"), "*.gz");
});

test("fileViewerFileType_NameWithoutDot_ReturnsLowerCasedName", () => {
  assert.equal(fileViewerFileType("Dockerfile"), "dockerfile");
});
