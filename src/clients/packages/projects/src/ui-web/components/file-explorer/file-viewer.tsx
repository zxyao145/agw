import { Plus } from "lucide-react";
import { CodeViewerProps as FileViewerProps, CommentSide, LineComment } from "./types";
import React from "react";
import { cn } from "@agw/components";
import { CommentSection } from "./comment-section";

function FileViewer({
  content,
  lines: providedLines,
  filePath,
  comments,
  setComments,
  isDiffView,
  commentSide = CommentSide.Current,
  diffScope,
}: FileViewerProps) {
  const [activeCommentLine, setActiveCommentLine] = React.useState<number | null>(null);
  const [hoveredLine, setHoveredLine] = React.useState<number | null>(null);

  const lines = React.useMemo(
    () =>
      providedLines ??
      content.split("\n").map((line, index) => ({
        content: line,
        kind: "context" as const,
        lineNumber: index + 1,
      })),
    [content, providedLines],
  );
  const lineNumberWidth = React.useMemo(() => {
    const largestLineNumber = lines.reduce(
      (largest, line) => Math.max(largest, line.lineNumber ?? 0),
      1,
    );
    return String(largestLineNumber).length;
  }, [lines]);
  const commentsByLine = React.useMemo(() => {
    const groupedComments = new Map<number, LineComment[]>();
    comments.forEach((comment) => {
      if (
        comment.filePath !== filePath ||
        comment.side !== commentSide ||
        comment.diffScope !== diffScope
      ) {
        return;
      }

      const lineComments = groupedComments.get(comment.lineNumber) ?? [];
      lineComments.push(comment);
      groupedComments.set(comment.lineNumber, lineComments);
    });
    return groupedComments;
  }, [commentSide, comments, diffScope, filePath]);

  const handleAddComment = (lineNumber: number, content: string) => {
    if (!content.trim()) return;

    const newComment: LineComment = {
      id: `${Date.now()}-${Math.random().toString(36).substr(2, 9)}`,
      side: commentSide,
      ...(diffScope ? { diffScope } : {}),
      filePath: filePath,
      lineNumber,
      content: content.trim(),
      timestamp: new Date(),
    };

    setComments((prev) => [...prev, newComment]);
    setActiveCommentLine(null);
  };

  const handleDeleteComment = (commentId: string) => {
    setComments((prev) => prev.filter((c) => c.id !== commentId));
  };

  const handleUpdateComment = (commentId: string, newContent: string) => {
    setComments((prev) =>
      prev.map((c) => (c.id === commentId ? { ...c, content: newContent } : c)),
    );
  };

  return (
    <div className={cn("font-mono text-sm", isDiffView && "w-full min-w-0")}>
      {lines.map((line, index) => {
        if (line.kind === "hunk") {
          return (
            <div
              key={index}
              className="flex min-h-7 border-y border-blue-200 bg-blue-50 text-blue-800 dark:border-blue-900 dark:bg-blue-950/40 dark:text-blue-200"
            >
              <div
                className="shrink-0 border-r border-blue-200 bg-blue-100/80 dark:border-blue-900 dark:bg-blue-900/40"
                style={{ minWidth: `${lineNumberWidth + 2}ch` }}
              />
              <div className="flex w-6 shrink-0 items-center justify-center border-r border-blue-200 text-xs dark:border-blue-900">
                ···
              </div>
              <pre
                className={cn(
                  "flex-1 px-4 py-1 whitespace-pre",
                  isDiffView ? "min-w-max" : "min-w-0 overflow-x-auto agw-scrollbar",
                )}
              >
                <code>{line.content}</code>
              </pre>
            </div>
          );
        }

        if (line.kind === "placeholder") {
          return (
            <div key={index} className="flex min-h-6 bg-muted/20" aria-hidden="true">
              <div
                className="shrink-0 border-r border-border bg-muted/30"
                style={{ minWidth: `${lineNumberWidth + 2}ch` }}
              />
              <div className="w-6 shrink-0 border-r border-border bg-muted/30" />
            </div>
          );
        }

        if (line.kind === "annotation") {
          return (
            <div
              key={index}
              className="flex min-h-6 bg-muted/30 text-xs italic text-muted-foreground"
            >
              <div className="sticky left-0 z-10 flex shrink-0 bg-muted">
                <div
                  className="shrink-0 border-r border-border"
                  style={{ minWidth: `${lineNumberWidth + 2}ch` }}
                />
                <div
                  className="flex w-6 shrink-0 items-center justify-center border-r border-border"
                  aria-hidden="true"
                >
                  \
                </div>
              </div>
              <pre
                className={cn(
                  "flex-1 px-4 py-0.5 whitespace-pre",
                  isDiffView ? "min-w-max" : "min-w-0 overflow-x-auto agw-scrollbar",
                )}
              >
                <code>{line.content}</code>
              </pre>
            </div>
          );
        }

        const lineNumber = line.lineNumber ?? index + 1;
        const lineComments = commentsByLine.get(lineNumber) ?? [];
        const isHovered = hoveredLine === lineNumber;
        const isCommentActive = activeCommentLine === lineNumber;
        const hasComments = (lineComments?.length ?? 0) > 0;
        const isAddition = line.kind === "addition";
        const isDeletion = line.kind === "deletion";
        const marker = isAddition ? "+" : isDeletion ? "−" : "";

        return (
          <div key={index}>
            <div
              className={cn(
                "flex min-h-6 group transition-colors",
                isAddition && "bg-green-50 dark:bg-green-950/30",
                isDeletion && "bg-red-50 dark:bg-red-950/30",
                !isAddition && !isDeletion && "hover:bg-muted/50",
                (isCommentActive || hasComments) && "bg-muted/50",
              )}
              onMouseEnter={() => setHoveredLine(lineNumber)}
              onMouseLeave={() => setHoveredLine(null)}
            >
              {/* Line number with add comment button */}
              <div className="sticky left-0 z-10 flex shrink-0 select-none items-center bg-background">
                {/* Line number */}
                <div
                  className={cn(
                    "border-r border-border px-2 py-0.5 text-right text-muted-foreground",
                    isAddition &&
                      "bg-green-100/80 text-green-800 dark:bg-green-900/40 dark:text-green-200",
                    isDeletion && "bg-red-100/80 text-red-800 dark:bg-red-900/40 dark:text-red-200",
                  )}
                  style={{ minWidth: `${lineNumberWidth + 2}ch` }}
                >
                  {lineNumber}
                </div>

                <div
                  className={cn(
                    "flex w-6 shrink-0 items-center justify-center border-r border-border py-0.5 text-muted-foreground",
                    isAddition &&
                      "bg-green-100/80 text-green-800 dark:bg-green-900/40 dark:text-green-200",
                    isDeletion && "bg-red-100/80 text-red-800 dark:bg-red-900/40 dark:text-red-200",
                  )}
                  aria-hidden="true"
                >
                  {marker}
                </div>

                {/* add comment button */}
                <div className="absolute right-0 flex w-6 items-center justify-center">
                  {!hasComments && (isHovered || isCommentActive) && (
                    <button
                      onClick={() => {
                        if (!hasComments) {
                          setActiveCommentLine(isCommentActive ? null : lineNumber);
                        }
                      }}
                      className={cn(
                        "w-5 h-5 flex items-center justify-center rounded text-primary-foreground transition-colors",
                        isCommentActive
                          ? "bg-primary"
                          : "bg-blue-500 hover:bg-blue-600 cursor-pointer",
                      )}
                      title="Add comment"
                    >
                      <Plus className="h-3 w-3" />
                    </button>
                  )}
                </div>
              </div>

              {/* Code content */}
              <pre
                className={cn(
                  "flex-1 px-4 py-0.5 whitespace-pre",
                  isDiffView ? "min-w-max" : "min-w-0 overflow-x-auto agw-scrollbar",
                )}
              >
                <code>{line.content || " "}</code>
              </pre>
            </div>

            {/* Comment section */}
            {(hasComments || isCommentActive) && (
              <div
                className="relative border-y border-border bg-muted/50"
                style={{ marginLeft: `calc(${lineNumberWidth + 2}ch + 1.5rem)` }}
              >
                {/* Left accent bar */}
                <div className="absolute left-0 top-0 bottom-0 w-1 bg-blue-500" />

                <CommentSection
                  lineComments={lineComments}
                  isCommentActive={isCommentActive}
                  onAddComment={(content) => handleAddComment(lineNumber, content)}
                  onDeleteComment={handleDeleteComment}
                  onUpdateComment={handleUpdateComment}
                  isDiffView={isDiffView}
                  commentSide={commentSide}
                />
              </div>
            )}
          </div>
        );
      })}
    </div>
  );
}

export default React.memo(FileViewer);
