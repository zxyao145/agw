"use client";

import * as React from "react";
import { cn } from "@/lib/utils";
import FileViewer from "./file-viewer";
import { CommentSide, DiffViewerProps, LineComment } from "./types";

/**
 * Parse unified diff format into original and modified file contents
 */
function parseDiffToFiles(diffText: string): { original: string; modified: string } {
  const lines = diffText.split("\n");
  const originalLines: string[] = [];
  const modifiedLines: string[] = [];

  for (const line of lines) {
    // Skip diff header lines
    if (
      line.startsWith("diff --git") ||
      line.startsWith("index ") ||
      line.startsWith("--- ") ||
      line.startsWith("+++ ") ||
      line.startsWith("@@")
    ) {
      continue;
    }

    if (line.startsWith("-")) {
      // Removed line (only in original)
      originalLines.push(line.substring(1));
    } else if (line.startsWith("+")) {
      // Added line (only in modified)
      modifiedLines.push(line.substring(1));
    } else if (line.startsWith(" ")) {
      // Context line (in both)
      const content = line.substring(1);
      originalLines.push(content);
      modifiedLines.push(content);
    }
    // Empty lines are preserved as-is
  }

  return {
    original: originalLines.join("\n"),
    modified: modifiedLines.join("\n"),
  };
}

export function DiffViewer({
  diff,
  className,
  filePath = "",
  comments = [],
  setComments,
}: DiffViewerProps) {
  const { original, modified } = React.useMemo(() => parseDiffToFiles(diff), [diff]);

  // Filter comments for each side
  const originalComments = React.useMemo(
    () => comments.filter((c) => c.side === CommentSide.Original),
    [comments],
  );

  const modifiedComments = React.useMemo(
    () => comments.filter((c) => c.side === CommentSide.Modified),
    [comments],
  );

  // FileViewer updates one side at a time, so merge the edited slice back into the full list.
  const handleSetOriginalComments = React.useCallback(
    (setter: React.SetStateAction<LineComment[]>) => {
      if (!setComments) return;
      // FileViewer will call this with a setter that operates on the filtered comments
      // We need to translate that to operate on the full comments array
      setComments((prev) => {
        const currentOriginalComments = prev.filter((c) => c.side === CommentSide.Original);
        const otherComments = prev.filter((c) => c.side !== CommentSide.Original);

        const newOriginalComments =
          typeof setter === "function" ? setter(currentOriginalComments) : setter;

        // Merge back with other side's comments
        return [...otherComments, ...newOriginalComments];
      });
    },
    [setComments],
  );

  const handleSetModifiedComments = React.useCallback(
    (setter: React.SetStateAction<LineComment[]>) => {
      if (!setComments) return;
      setComments((prev) => {
        const currentModifiedComments = prev.filter((c) => c.side === CommentSide.Modified);
        const otherComments = prev.filter((c) => c.side !== CommentSide.Modified);

        const newModifiedComments =
          typeof setter === "function" ? setter(currentModifiedComments) : setter;

        // Merge back with other side's comments
        return [...otherComments, ...newModifiedComments];
      });
    },
    [setComments],
  );

  if (!diff.trim()) {
    return (
      <div
        className={cn("flex items-center justify-center h-full text-muted-foreground", className)}
      >
        <p className="text-sm">No changes detected</p>
      </div>
    );
  }

  return (
    <div className={cn("flex flex-col h-full overflow-hidden", className)}>
      {/* Header row */}
      <div className="flex shrink-0 border-b border-border">
        <div className="flex-1 bg-red-50 dark:bg-red-950 px-3 py-1.5 border-r border-border text-sm font-medium text-red-900 dark:text-red-100">
          Original
        </div>
        <div className="flex-1 bg-green-50 dark:bg-green-950 px-3 py-1.5 text-sm font-medium text-green-900 dark:text-green-100">
          Modified
        </div>
      </div>

      {/* File viewers */}
      <div className="flex-1 flex overflow-hidden">
        {/* Original side */}
        <div className="flex-1 overflow-auto border-r border-border">
          <FileViewer
            content={original}
            filePath={filePath}
            comments={originalComments}
            setComments={handleSetOriginalComments}
            isDiffView={true}
            commentSide={CommentSide.Original}
          />
        </div>

        {/* Modified side */}
        <div className="flex-1 overflow-auto">
          <FileViewer
            content={modified}
            filePath={filePath}
            comments={modifiedComments}
            setComments={handleSetModifiedComments}
            isDiffView={true}
            commentSide={CommentSide.Modified}
          />
        </div>
      </div>
    </div>
  );
}
