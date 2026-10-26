// 行评论的匹配、分组、创建与行号更新（设计文档第 6、7 节），纯函数。Matching, grouping, creating and remapping line
// comments (design sections 6 and 7); pure functions.
import { type LineChange, remapLine } from "@agw/code-view";

import type { GitDiffScope } from "../../../services/files";
import type { ProjectFileScope } from "./project-file-scope";
import type { CommentSide, LineComment } from "./types";

/** 一个编辑器显示的评论：文件、目录与项目、side 与 diff 范围。The comments one editor shows. */
export interface LineCommentTarget {
  scope: ProjectFileScope;
  filePath: string;
  side: CommentSide;
  diffScope?: GitDiffScope;
}

/**
 * 评论属于目标：文件、目录、side 与 diff 范围相同；两边都有项目时项目也相同。
 * A comment belongs to the target when file, directory, side and diff scope match, and the project matches when both
 * have one.
 */
export function isCommentForTarget(comment: LineComment, target: LineCommentTarget): boolean {
  return (
    comment.filePath === target.filePath &&
    (comment.directoryId ?? null) === (target.scope.directoryId ?? null) &&
    (comment.projectId == null ||
      target.scope.projectId == null ||
      comment.projectId === target.scope.projectId) &&
    comment.side === target.side &&
    comment.diffScope === target.diffScope
  );
}

/** 目标的评论，按行号分组，行号从小到大。The target's comments grouped by line, in line order. */
export function groupCommentsByLine(
  comments: readonly LineComment[],
  target: LineCommentTarget,
): ReadonlyMap<number, readonly LineComment[]> {
  const groups = new Map<number, LineComment[]>();
  for (const comment of comments) {
    if (!isCommentForTarget(comment, target)) continue;
    const group = groups.get(comment.lineNumber);
    if (group) group.push(comment);
    else groups.set(comment.lineNumber, [comment]);
  }
  return new Map([...groups].sort(([left], [right]) => left - right));
}

/**
 * 内容变化后目标评论的行号；没有行号变化时返回原数组。
 * The target's comment lines after a content change; returns the same array when no line moves.
 */
export function remapCommentLines(
  comments: LineComment[],
  target: LineCommentTarget,
  changes: readonly LineChange[],
): LineComment[] {
  let moved = false;
  const next = comments.map((comment) => {
    if (!isCommentForTarget(comment, target)) return comment;
    const lineNumber = remapLine(comment.lineNumber, changes);
    if (lineNumber === comment.lineNumber) return comment;
    moved = true;
    return { ...comment, lineNumber };
  });
  return moved ? next : comments;
}

/** 目标某一行的新评论，内容去掉首尾空白。A new comment on one line of the target, with its content trimmed. */
export function createLineComment(
  target: LineCommentTarget,
  lineNumber: number,
  content: string,
  id: string,
  timestamp: Date,
): LineComment {
  return {
    ...target.scope,
    id,
    side: target.side,
    ...(target.diffScope ? { diffScope: target.diffScope } : {}),
    filePath: target.filePath,
    lineNumber,
    content: content.trim(),
    timestamp,
  };
}
