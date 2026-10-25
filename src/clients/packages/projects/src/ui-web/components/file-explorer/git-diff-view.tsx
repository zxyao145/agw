import type * as React from "react";
import { CodeDiffViewer, CodeViewer } from "@agw/code-view";

import type { GitDiffResponse, GitDiffScope } from "../../../services/files";
import { CommentSide, type LineComment } from "./types";
import { useLineCommentZones } from "./use-line-comment-zones";

/** 两侧的标题，沿用原来的 diff 视图。Side titles, kept from the previous diff view. */
const SIDE_LABELS: Record<GitDiffScope | "all", { original: string; modified: string }> = {
  staged: { original: "HEAD", modified: "Staged" },
  unstaged: { original: "Staged", modified: "Working Tree" },
  all: { original: "Original", modified: "Modified" },
};

interface GitDiffViewProps {
  diff: GitDiffResponse;
  filePath: string;
  scope?: GitDiffScope;
  comments: LineComment[];
  setComments: React.Dispatch<React.SetStateAction<LineComment[]>>;
}

/**
 * Only diff 的内容（设计文档第 7 节）：二进制文件、未改动的文件、只有元信息的 diff，其他情况为左右两栏的 Monaco diff。
 * The Only diff content (design section 7): binary files, unchanged files, metadata-only diffs, and otherwise a
 * side-by-side Monaco diff.
 */
export function GitDiffView({ diff, filePath, scope, comments, setComments }: GitDiffViewProps) {
  // diff 两侧只读，行号不会随编辑变化。Both diff sides are read-only, so no edit moves lines.
  const originalZones = useLineCommentZones({
    comments,
    setComments,
    filePath,
    side: CommentSide.Original,
    diffScope: scope,
    isDiffView: true,
  }).side;
  const modifiedZones = useLineCommentZones({
    comments,
    setComments,
    filePath,
    side: CommentSide.Modified,
    diffScope: scope,
    isDiffView: true,
  }).side;

  if (diff.isBinary) {
    return (
      <div className="flex h-full min-h-0 flex-col gap-3 overflow-auto p-4 agw-scrollbar">
        <p className="rounded bg-muted/50 p-3 text-sm text-muted-foreground">
          Binary file; its contents are not shown.
        </p>
        <pre aria-label="Git diff" className="whitespace-pre-wrap font-mono text-xs">
          {diff.diff}
        </pre>
      </div>
    );
  }

  if (diff.unchanged) {
    return (
      <div className="flex h-full min-h-0 flex-col">
        <p className="m-4 mb-2 rounded bg-muted/50 p-3 text-sm text-muted-foreground">
          No changes detected
        </p>
        <div className="min-h-0 flex-1">
          <CodeViewer
            content={{ kind: "text", text: diff.originalContent ?? diff.modifiedContent ?? "" }}
            filePath={filePath}
            ariaLabel={`Unchanged ${filePath}`}
            wordWrap={false}
            {...originalZones}
          />
        </div>
      </div>
    );
  }

  // 两侧文字相同而 diff 不为空：只有文件模式等元信息改动。Equal texts with a non-empty diff: only metadata such as the
  // file mode changed.
  if (diff.originalContent === diff.modifiedContent) {
    return (
      <div className="h-full min-h-0 overflow-auto p-4 agw-scrollbar">
        <pre aria-label="Git diff metadata" className="whitespace-pre-wrap font-mono text-xs">
          {diff.diff}
        </pre>
      </div>
    );
  }

  const labels = SIDE_LABELS[scope ?? "all"];
  return (
    <CodeDiffViewer
      key={scope ?? "all"}
      filePath={filePath}
      original={{
        text: diff.originalContent ?? "",
        label: labels.original,
        present: diff.originalContent !== null,
        ...originalZones,
      }}
      modified={{
        text: diff.modifiedContent ?? "",
        label: labels.modified,
        present: diff.modifiedContent !== null,
        ...modifiedZones,
      }}
    />
  );
}
