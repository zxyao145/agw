import type { LineComment } from "@agw/projects";

type FileCommentPromptItem = {
  projectRelativePath: string;
  fileVersion: "before_change" | "after_change";
  lineNumber: number;
  diffScope?: NonNullable<LineComment["diffScope"]>;
  comment: string;
};

function normalizeProjectRelativePath(filePath: string): string {
  return filePath.replace(/\\/gu, "/").replace(/^\.\/+/, "");
}

function toPromptItem(comment: LineComment): FileCommentPromptItem {
  return {
    projectRelativePath: normalizeProjectRelativePath(comment.filePath),
    fileVersion: comment.side === "original" ? "before_change" : "after_change",
    lineNumber: comment.lineNumber,
    ...(comment.diffScope ? { diffScope: comment.diffScope } : {}),
    comment: comment.content,
  };
}

export function buildFileCommentPrompt(input: string, comments: readonly LineComment[]): string {
  const trimmedInput = input.trim();
  if (comments.length === 0) {
    return trimmedInput;
  }

  const commentBlock = `<file_comments>\n${JSON.stringify(comments.map(toPromptItem), null, 2)}\n</file_comments>`;
  return trimmedInput ? `${trimmedInput}\n\n${commentBlock}` : commentBlock;
}
