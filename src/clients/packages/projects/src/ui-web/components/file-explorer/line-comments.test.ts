import assert from "node:assert/strict";
import test from "node:test";

import {
  createLineComment,
  groupCommentsByLine,
  isCommentForTarget,
  type LineCommentTarget,
  remapCommentLines,
} from "./line-comments";
import type { LineComment } from "./types";

const TARGET: LineCommentTarget = {
  scope: { projectId: "project-a", directoryId: "directory-a", directoryName: "app" },
  filePath: "src/app.ts",
  side: "modified",
  diffScope: "staged",
};

function comment(overrides: Partial<LineComment>): LineComment {
  return {
    projectId: "project-a",
    directoryId: "directory-a",
    id: "comment",
    side: "modified",
    diffScope: "staged",
    filePath: "src/app.ts",
    lineNumber: 3,
    content: "check this",
    timestamp: new Date("2026-10-25T00:00:00Z"),
    ...overrides,
  };
}

test("isCommentForTarget_SameFileDirectorySideAndScope_Matches", () => {
  assert.equal(isCommentForTarget(comment({}), TARGET), true);
  // 任一方没有项目时不比较项目。The project is compared only when both have one.
  assert.equal(isCommentForTarget(comment({ projectId: undefined }), TARGET), true);
  assert.equal(
    isCommentForTarget(comment({}), { ...TARGET, scope: { directoryId: "directory-a" } }),
    true,
  );
});

test("isCommentForTarget_AnyDifferentKey_DoesNotMatch", () => {
  for (const other of [
    comment({ filePath: "src/other.ts" }),
    comment({ directoryId: "directory-b" }),
    comment({ directoryId: null }),
    comment({ projectId: "project-b" }),
    comment({ side: "original" }),
    comment({ diffScope: "unstaged" }),
    comment({ diffScope: undefined }),
  ]) {
    assert.equal(isCommentForTarget(other, TARGET), false, JSON.stringify(other));
  }
});

test("groupCommentsByLine_MixedComments_GroupsTargetCommentsInLineOrder", () => {
  const groups = groupCommentsByLine(
    [
      comment({ id: "a", lineNumber: 9 }),
      comment({ id: "b", lineNumber: 2 }),
      comment({ id: "c", lineNumber: 9 }),
      comment({ id: "d", lineNumber: 4, side: "original" }),
    ],
    TARGET,
  );
  assert.deepEqual(
    [...groups].map(([line, comments]) => [line, comments.map((item) => item.id)]),
    [
      [2, ["b"]],
      [9, ["a", "c"]],
    ],
  );
});

test("createLineComment_DiffTarget_CopiesScopeAndTrimsContent", () => {
  const timestamp = new Date("2026-10-25T08:00:00Z");
  assert.deepEqual(createLineComment(TARGET, 7, "  why?  ", "id-1", timestamp), {
    projectId: "project-a",
    directoryId: "directory-a",
    directoryName: "app",
    id: "id-1",
    side: "modified",
    diffScope: "staged",
    filePath: "src/app.ts",
    lineNumber: 7,
    content: "why?",
    timestamp,
  });
});

test("createLineComment_SourceTarget_HasNoDiffScope", () => {
  const created = createLineComment(
    { scope: {}, filePath: "readme.md", side: "current" },
    1,
    "note",
    "id-2",
    new Date(0),
  );
  assert.equal("diffScope" in created, false);
  assert.equal(created.side, "current");
});

test("remapCommentLines_LinesInsertedAbove_MovesOnlyTargetComments", () => {
  const moving = comment({ id: "moving", lineNumber: 5 });
  const above = comment({ id: "above", lineNumber: 2 });
  const otherFile = comment({ id: "other", filePath: "src/other.ts", lineNumber: 5 });
  const comments = [moving, above, otherFile];
  const changes = [{ startLineNumber: 3, endLineNumber: 3, insertedLineCount: 2 }];
  const remapped = remapCommentLines(comments, TARGET, changes);
  assert.deepEqual(
    remapped.map((item) => [item.id, item.lineNumber]),
    [
      ["moving", 7],
      ["above", 2],
      ["other", 5],
    ],
  );
  // 没有移动的评论保持同一个对象。Comments that do not move stay the same objects.
  assert.equal(remapped[1], above);
  assert.equal(remapped[2], otherFile);
});

test("remapCommentLines_NoLineMoves_ReturnsTheSameArray", () => {
  const comments = [comment({ lineNumber: 2 })];
  const changes = [{ startLineNumber: 5, endLineNumber: 5, insertedLineCount: 1 }];
  assert.equal(remapCommentLines(comments, TARGET, changes), comments);
});
