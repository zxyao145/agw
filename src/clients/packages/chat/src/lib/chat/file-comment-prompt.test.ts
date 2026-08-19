import assert from "node:assert/strict";
import test from "node:test";

import type { LineComment } from "@agw/projects";
import { buildFileCommentPrompt } from "./file-comment-prompt";

function createComment(overrides: Partial<LineComment>): LineComment {
  return {
    id: "comment-1",
    side: "current",
    filePath: "src/example.ts",
    lineNumber: 1,
    content: "Review this line",
    timestamp: new Date("2026-08-19T00:00:00Z"),
    ...overrides,
  };
}

function readPayload(prompt: string): Array<Record<string, unknown>> {
  const match = prompt.match(/<file_comments>\n([\s\S]+)\n<\/file_comments>$/u);
  assert.ok(match);
  return JSON.parse(match[1]) as Array<Record<string, unknown>>;
}

test("file comments use explicit project-relative path and file version fields", () => {
  const prompt = buildFileCommentPrompt("Please apply these changes", [
    createComment({
      id: "before",
      side: "original",
      filePath: "src/server/format.sh",
      lineNumber: 13,
      content: "Comment out this line",
      diffScope: "unstaged",
    }),
    createComment({
      id: "after",
      side: "modified",
      filePath: "src/clients/web/page.tsx",
      lineNumber: 27,
      content: "Keep this replacement",
      diffScope: "staged",
    }),
  ]);

  assert.ok(prompt.startsWith("Please apply these changes\n\n<file_comments>\n"));
  assert.deepEqual(readPayload(prompt), [
    {
      projectRelativePath: "src/server/format.sh",
      fileVersion: "before_change",
      lineNumber: 13,
      diffScope: "unstaged",
      comment: "Comment out this line",
    },
    {
      projectRelativePath: "src/clients/web/page.tsx",
      fileVersion: "after_change",
      lineNumber: 27,
      diffScope: "staged",
      comment: "Keep this replacement",
    },
  ]);
  assert.equal(
    readPayload(prompt).some((item) => "side" in item),
    false,
  );
});

test("current-file comments are after-change references without a diff scope", () => {
  const prompt = buildFileCommentPrompt("", [
    createComment({
      side: "current",
      filePath: ".\\src\\nested\\file.ts",
      lineNumber: 8,
    }),
  ]);

  assert.deepEqual(readPayload(prompt), [
    {
      projectRelativePath: "src/nested/file.ts",
      fileVersion: "after_change",
      lineNumber: 8,
      comment: "Review this line",
    },
  ]);
});

test("prompt composition supports text-only, comment-only, and empty input", () => {
  assert.equal(buildFileCommentPrompt("  text only  ", []), "text only");
  assert.equal(buildFileCommentPrompt("   ", []), "");
  assert.match(buildFileCommentPrompt("", [createComment({})]), /^<file_comments>/u);
});
