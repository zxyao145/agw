import { Button } from "@/components/ui/button";
import { MessageSquare, Plus, Send, X } from "lucide-react";
import { CodeViewerProps as FileViewerProps, LineComment } from "../../types";
import React from "react";
import { cn } from "@/lib/utils";
import { Textarea } from "@/components/ui/textarea";

export default function FileViewer({
  content,
  filePath,
  comments,
  setComments,
  isDiffView,
  isOriginal,
}: FileViewerProps) {
  const [activeCommentLine, setActiveCommentLine] = React.useState<
    number | null
  >(null);
  const [commentInput, setCommentInput] = React.useState("");
  const [hoveredLine, setHoveredLine] = React.useState<number | null>(null);

  const lines = React.useMemo(() => content.split("\n"), [content]);
  const lineNumberWidth = React.useMemo(
    () => String(lines.length).length,
    [lines.length],
  );

  const getCommentsForLine = React.useCallback(
    (lineNumber: number) => comments.filter((c) => c.lineIndex === lineNumber),
    [comments],
  );

  const handleAddComment = (lineNumber: number) => {
    if (!commentInput.trim()) return;

    const newComment: LineComment = {
      id: `${Date.now()}-${Math.random().toString(36).substr(2, 9)}`,
      // In diff view: Original side = isAfter=false, Modified side = isAfter=true
      // In normal view: isAfter=true
      isAfter: isDiffView ? !isOriginal : true,
      filePath: filePath,
      lineIndex: lineNumber,
      content: commentInput.trim(),
      timestamp: new Date(),
    };

    setComments((prev) => [...prev, newComment]);
    setCommentInput("");
    setActiveCommentLine(null);
  };

  const handleDeleteComment = (commentId: string) => {
    setComments((prev) => prev.filter((c) => c.id !== commentId));
  };

  return (
    <div className="font-mono text-sm">
      {lines.map((line, index) => {
        const lineNumber = index + 1;
        const lineComments = getCommentsForLine(lineNumber);
        const isHovered = hoveredLine === lineNumber;
        const isCommentActive = activeCommentLine === lineNumber;

        return (
          <div key={index}>
            <div
              className={cn(
                "flex group hover:bg-muted/50 transition-colors",
                isCommentActive && "bg-muted/50",
              )}
              onMouseEnter={() => setHoveredLine(lineNumber)}
              onMouseLeave={() => setHoveredLine(null)}
            >
              {/* Line number with add comment button */}
              <div className="flex items-center shrink-0 select-none sticky left-0 bg-inherit">
                <div className="w-6 flex items-center justify-center">
                  {(isHovered || isCommentActive) && (
                    <button
                      onClick={() =>
                        setActiveCommentLine(
                          isCommentActive ? null : lineNumber,
                        )
                      }
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
                <div
                  className="px-3 py-0.5 text-right text-muted-foreground border-r border-border"
                  style={{ minWidth: `${lineNumberWidth + 2}ch` }}
                >
                  {lineNumber}
                </div>
              </div>

              {/* Code content */}
              <pre className="flex-1 px-4 py-0.5 whitespace-pre overflow-x-auto">
                <code>{line || " "}</code>
              </pre>

              {/* Comment indicator */}
              {lineComments.length > 0 && !isCommentActive && (
                <div className="flex items-center pr-2">
                  <button
                    onClick={() => setActiveCommentLine(lineNumber)}
                    className="flex items-center gap-1 text-xs text-muted-foreground hover:text-foreground"
                  >
                    <MessageSquare className="h-3 w-3" />
                    <span>{lineComments.length}</span>
                  </button>
                </div>
              )}
            </div>

            {/* Comment section */}
            {(isCommentActive ||
              (lineComments.length > 0 &&
                activeCommentLine === lineNumber)) && (
              <div
                className="border-y border-border bg-muted/30 ml-6"
                style={{ marginLeft: `${lineNumberWidth + 5}ch` }}
              >
                {/* Existing comments */}
                {lineComments.map((comment) => (
                  <div
                    key={comment.id}
                    className="p-3 border-b border-border last:border-b-0"
                  >
                    <div className="flex items-start justify-between gap-2">
                      <div className="flex-1">
                        <div className="flex items-center gap-2 mb-1">
                          <span className="text-xs text-muted-foreground">
                            {comment.timestamp.toLocaleTimeString()}
                          </span>
                        </div>
                        <p className="text-sm whitespace-pre-wrap">
                          {comment.content}
                        </p>
                      </div>
                      <button
                        onClick={() => handleDeleteComment(comment.id)}
                        className="text-muted-foreground hover:text-destructive p-1 cursor-pointer"
                        title="Delete comment"
                      >
                        <X className="h-3 w-3" />
                      </button>
                    </div>
                  </div>
                ))}

                {/* Add comment input */}
                {isCommentActive && (
                  <div className="p-3">
                    <Textarea
                      value={commentInput}
                      onChange={(e) => setCommentInput(e.target.value)}
                      placeholder="Write a comment..."
                      className="min-h-20 text-sm resize-none"
                      autoFocus
                      onKeyDown={(e) => {
                        if (e.key === "Enter" && (e.ctrlKey || e.metaKey)) {
                          handleAddComment(lineNumber);
                        }
                        if (e.key === "Escape") {
                          setActiveCommentLine(null);
                          setCommentInput("");
                        }
                      }}
                    />
                    <div className="flex items-center justify-between mt-2">
                      <span className="text-xs text-muted-foreground">
                        Ctrl+Enter to submit, Esc to cancel
                      </span>
                      <div className="flex gap-2">
                        <Button
                          variant="ghost"
                          size="sm"
                          onClick={() => {
                            setActiveCommentLine(null);
                            setCommentInput("");
                          }}
                        >
                          Cancel
                        </Button>
                        <Button
                          size="sm"
                          onClick={() => handleAddComment(lineNumber)}
                          disabled={!commentInput.trim()}
                        >
                          <Send className="h-3 w-3 mr-1" />
                          Comment
                        </Button>
                      </div>
                    </div>
                  </div>
                )}
              </div>
            )}
          </div>
        );
      })}
    </div>
  );
}