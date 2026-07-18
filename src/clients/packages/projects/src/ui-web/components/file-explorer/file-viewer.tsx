import { Plus } from "lucide-react";
import { CodeViewerProps as FileViewerProps, CommentSide, LineComment } from "./types";
import React from "react";
import { cn } from "@agw/components";
import { CommentSection } from "./comment-section";

export default function FileViewer({
  content,
  filePath,
  comments,
  setComments,
  isDiffView,
  commentSide = CommentSide.Current,
}: FileViewerProps) {
  const [activeCommentLine, setActiveCommentLine] = React.useState<number | null>(null);
  const [hoveredLine, setHoveredLine] = React.useState<number | null>(null);

  const lines = React.useMemo(() => content.split("\n"), [content]);
  const lineNumberWidth = React.useMemo(() => String(lines.length).length, [lines.length]);

  const getCommentsForLine = React.useCallback(
    (lineNumber: number) => comments.filter((c) => c.lineNumber === lineNumber),
    [comments],
  );

  const handleAddComment = (lineNumber: number, content: string) => {
    if (!content.trim()) return;

    const newComment: LineComment = {
      id: `${Date.now()}-${Math.random().toString(36).substr(2, 9)}`,
      side: commentSide,
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
    <div className="font-mono text-sm">
      {lines.map((line, index) => {
        const lineNumber = index + 1;
        const lineComments = getCommentsForLine(lineNumber);
        const isHovered = hoveredLine === lineNumber;
        const isCommentActive = activeCommentLine === lineNumber;
        const hasComments = (lineComments?.length ?? 0) > 0;

        return (
          <div key={index}>
            <div
              className={cn(
                "flex group hover:bg-muted/50 transition-colors",
                (isCommentActive || hasComments) && "bg-muted/50",
              )}
              onMouseEnter={() => setHoveredLine(lineNumber)}
              onMouseLeave={() => setHoveredLine(null)}
            >
              {/* Line number with add comment button */}
              <div className="flex items-center shrink-0 select-none sticky left-0 bg-inherit">
                {/* Line number */}
                <div
                  className="px-3 py-0.5 text-right text-muted-foreground border-r border-border"
                  style={{ minWidth: `${lineNumberWidth + 2}ch` }}
                >
                  {lineNumber}
                </div>

                {/* add comment button */}
                <div
                  className={`w-6 flex items-center justify-center absolute -right-${lineNumberWidth}`}
                >
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
              <pre className="flex-1 px-4 py-0.5 whitespace-pre overflow-x-auto">
                <code>{line || " "}</code>
              </pre>
            </div>

            {/* Comment section */}
            {(hasComments || isCommentActive) && (
              <div
                className="relative border-y border-border bg-muted/50"
                style={{ marginLeft: `${lineNumberWidth}ch` }}
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
