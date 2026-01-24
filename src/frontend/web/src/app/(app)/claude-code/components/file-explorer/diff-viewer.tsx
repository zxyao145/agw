"use client";

import * as React from "react";
import { Plus, MessageSquare, X, Send } from "lucide-react";
import { cn } from "@/lib/utils";
import { Button } from "@/components/ui/button";
import { Textarea } from "@/components/ui/textarea";
import {
  DiffLine,
  DiffViewerProps,
  LineComment,
  DiffLineRowProps,
} from "../../types";

/**
 * Parse unified diff format into structured data
 */
function parseDiff(diffText: string): DiffLine[] {
  const lines = diffText.split("\n");
  const result: DiffLine[] = [];
  let oldLineNum = 0;
  let newLineNum = 0;

  for (const line of lines) {
    // Skip diff header lines (diff --git, index, ---, +++)
    if (
      line.startsWith("diff --git") ||
      line.startsWith("index ") ||
      line.startsWith("--- ") ||
      line.startsWith("+++ ")
    ) {
      result.push({ type: "header", content: line });
      continue;
    }

    // Parse hunk header (@@ -x,y +a,b @@)
    if (line.startsWith("@@")) {
      const match = line.match(/@@ -(\d+),?\d* \+(\d+),?\d* @@/);
      if (match) {
        oldLineNum = parseInt(match[1]);
        newLineNum = parseInt(match[2]);
      }
      result.push({ type: "header", content: line });
      continue;
    }

    // Parse content lines
    if (line.startsWith("+")) {
      result.push({
        type: "add",
        newLineNum: newLineNum++,
        content: line.substring(1),
      });
    } else if (line.startsWith("-")) {
      result.push({
        type: "remove",
        oldLineNum: oldLineNum++,
        content: line.substring(1),
      });
    } else {
      // Context line (starts with space or empty)
      result.push({
        type: "context",
        oldLineNum: oldLineNum++,
        newLineNum: newLineNum++,
        content: line.startsWith(" ") ? line.substring(1) : line,
      });
    }
  }

  return result;
}

/**
 * Split diff lines into old (left) and new (right) sides
 */
function splitDiffSides(
  lines: DiffLine[]
): { old: DiffLine[]; new: DiffLine[] } {
  const oldLines: DiffLine[] = [];
  const newLines: DiffLine[] = [];

  for (const line of lines) {
    if (line.type === "header") {
      oldLines.push(line);
      newLines.push(line);
    } else if (line.type === "remove") {
      oldLines.push(line);
      newLines.push({ type: "context", content: "" }); // Empty line on right
    } else if (line.type === "add") {
      oldLines.push({ type: "context", content: "" }); // Empty line on left
      newLines.push(line);
    } else {
      // context line appears on both sides
      oldLines.push(line);
      newLines.push(line);
    }
  }

  return { old: oldLines, new: newLines };
}

function DiffLineRow({
  oldLine,
  newLine,
  index,
  comments,
  isHovered,
  isCommentActive,
  commentInput,
  activeSide,
  onHover,
  onToggleComment,
  onCommentInputChange,
  onAddComment,
  onDeleteComment,
}: DiffLineRowProps) {
  // Filter comments for this line and side
  const oldComments = comments.filter(c => c.lineIndex === index && !c.isAfter);
  const newComments = comments.filter(c => c.lineIndex === index && c.isAfter);
  const totalComments = oldComments.length + newComments.length;

  return (
    <div>
      <div
        className="flex group"
        onMouseEnter={() => onHover(index)}
        onMouseLeave={() => onHover(null)}
      >
        {/* Left side - Old version */}
        <div
          className={cn(
            "flex-1 flex border-r",
            oldLine.type === "remove" && "bg-red-100 dark:bg-red-950/50",
            oldLine.type === "header" && "bg-muted/50 font-semibold",
            oldLine.content === "" && "min-h-5"
          )}
        >
          {/* Add comment button */}
          <div className="w-6 flex items-center justify-center shrink-0">
            {(isHovered || isCommentActive) && oldLine.type !== "header" && (
              <button
                onClick={() => onToggleComment(index, 'old')}
                className={cn(
                  "w-5 h-5 flex items-center justify-center rounded text-primary-foreground transition-colors",
                  activeSide === 'old'
                    ? "bg-primary"
                    : "bg-blue-500 hover:bg-blue-600"
                )}
                title="Add comment to Original"
              >
                <Plus className="h-3 w-3" />
              </button>
            )}
          </div>
          <span className="inline-block w-10 px-2 text-right text-muted-foreground select-none shrink-0 text-xs">
            {oldLine.oldLineNum}
          </span>
          <span className="px-2 flex-1 whitespace-pre-wrap break-all text-xs">
            {oldLine.content || " "}
          </span>
          {/* Comment indicator for old side */}
          {oldComments.length > 0 && !isCommentActive && (
            <div className="flex items-center pr-2">
              <button
                onClick={() => onToggleComment(index, 'old')}
                className="flex items-center gap-1 text-xs text-muted-foreground hover:text-foreground"
                title={`${oldComments.length} comment${oldComments.length > 1 ? 's' : ''} in Original`}
              >
                <MessageSquare className="h-3 w-3" />
                <span>{oldComments.length}</span>
              </button>
            </div>
          )}
        </div>

        {/* Right side - New version */}
        <div
          className={cn(
            "flex-1 flex",
            newLine.type === "add" && "bg-green-100 dark:bg-green-950/50",
            newLine.type === "header" && "bg-muted/50 font-semibold",
            newLine.content === "" && "min-h-5"
          )}
        >
          {/* Add comment button */}
          <div className="w-6 flex items-center justify-center shrink-0">
            {(isHovered || isCommentActive) && newLine.type !== "header" && (
              <button
                onClick={() => onToggleComment(index, 'new')}
                className={cn(
                  "w-5 h-5 flex items-center justify-center rounded text-primary-foreground transition-colors",
                  activeSide === 'new'
                    ? "bg-primary"
                    : "bg-blue-500 hover:bg-blue-600"
                )}
                title="Add comment to Modified"
              >
                <Plus className="h-3 w-3" />
              </button>
            )}
          </div>
          <span className="inline-block w-10 px-2 text-right text-muted-foreground select-none shrink-0 text-xs">
            {newLine.newLineNum}
          </span>
          <span className="px-2 flex-1 whitespace-pre-wrap break-all text-xs">
            {newLine.content || " "}
          </span>
          {/* Comment indicator for new side */}
          {newComments.length > 0 && !isCommentActive && (
            <div className="flex items-center pr-2">
              <button
                onClick={() => onToggleComment(index, 'new')}
                className="flex items-center gap-1 text-xs text-muted-foreground hover:text-foreground"
                title={`${newComments.length} comment${newComments.length > 1 ? 's' : ''} in Modified`}
              >
                <MessageSquare className="h-3 w-3" />
                <span>{newComments.length}</span>
              </button>
            </div>
          )}
        </div>
      </div>

      {/* Comment section */}
      {isCommentActive && (
        <div className="border-y border-border bg-muted/30 mx-6">
          {/* Existing comments for the active side */}
          {activeSide === 'old' && oldComments.map((comment) => (
            <div key={comment.id} className="p-3 border-b border-border last:border-b-0">
              <div className="flex items-start justify-between gap-2">
                <div className="flex-1">
                  <div className="flex items-center gap-2 mb-1">
                    <span className="text-xs text-muted-foreground">
                      {comment.timestamp.toLocaleTimeString()}
                    </span>
                    <span className="text-xs text-blue-600 dark:text-blue-400">
                      Original
                    </span>
                  </div>
                  <p className="text-sm whitespace-pre-wrap">{comment.content}</p>
                </div>
                <button
                  onClick={() => onDeleteComment(comment.id)}
                  className="text-muted-foreground hover:text-destructive p-1"
                  title="Delete comment"
                >
                  <X className="h-3 w-3" />
                </button>
              </div>
            </div>
          ))}
          {activeSide === 'new' && newComments.map((comment) => (
            <div key={comment.id} className="p-3 border-b border-border last:border-b-0">
              <div className="flex items-start justify-between gap-2">
                <div className="flex-1">
                  <div className="flex items-center gap-2 mb-1">
                    <span className="text-xs text-muted-foreground">
                      {comment.timestamp.toLocaleTimeString()}
                    </span>
                    <span className="text-xs text-green-600 dark:text-green-400">
                      Modified
                    </span>
                  </div>
                  <p className="text-sm whitespace-pre-wrap">{comment.content}</p>
                </div>
                <button
                  onClick={() => onDeleteComment(comment.id)}
                  className="text-muted-foreground hover:text-destructive p-1"
                  title="Delete comment"
                >
                  <X className="h-3 w-3" />
                </button>
              </div>
            </div>
          ))}

          {/* Add comment input */}
          <div className="p-3">
            <Textarea
              value={commentInput}
              onChange={(e) => onCommentInputChange(e.target.value)}
              placeholder={`Write a comment for ${activeSide === 'old' ? 'Original' : 'Modified'}...`}
              className="min-h-20 text-sm resize-none"
              autoFocus
              onKeyDown={(e) => {
                if (e.key === "Enter" && (e.ctrlKey || e.metaKey)) {
                  if (activeSide) onAddComment(index, activeSide);
                }
                if (e.key === "Escape") {
                  onToggleComment(null, 'old');
                  onCommentInputChange("");
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
                    onToggleComment(null, 'old');
                    onCommentInputChange("");
                  }}
                >
                  Cancel
                </Button>
                <Button
                  size="sm"
                  onClick={() => {
                    if (activeSide) onAddComment(index, activeSide);
                  }}
                  disabled={!commentInput.trim()}
                >
                  <Send className="h-3 w-3 mr-1" />
                  Comment
                </Button>
              </div>
            </div>
          </div>
        </div>
      )}
    </div>
  );
}

export function DiffViewer({ diff, className, filePath = '', comments = [], setComments }: DiffViewerProps) {
  const diffLines = React.useMemo(() => parseDiff(diff), [diff]);
  const { old: oldLines, new: newLines } = React.useMemo(
    () => splitDiffSides(diffLines),
    [diffLines]
  );

  const [activeCommentLine, setActiveCommentLine] = React.useState<number | null>(null);
  const [activeSide, setActiveSide] = React.useState<'old' | 'new' | null>(null);
  const [commentInput, setCommentInput] = React.useState("");
  const [hoveredLine, setHoveredLine] = React.useState<number | null>(null);

  const getCommentsForLine = React.useCallback(
    (lineIndex: number) => comments.filter((c) => c.lineIndex === lineIndex && c.filePath === filePath),
    [comments, filePath]
  );

  const handleAddComment = (lineIndex: number, side: 'old' | 'new') => {
    if (!commentInput.trim() || !setComments) return;

    const newComment: any = {
      id: `${Date.now()}-${Math.random().toString(36).substr(2, 9)}`,
      lineIndex,
      content: commentInput.trim(),
      timestamp: new Date(),
      filePath: filePath,
      // Original side = isAfter=false, Modified side = isAfter=true
      isAfter: side === 'new',
    };

    setComments((prev: any[]) => [...prev, newComment]);
    setCommentInput("");
    setActiveCommentLine(null);
    setActiveSide(null);
  };

  const handleDeleteComment = (commentId: string) => {
    if (!setComments) return;
    setComments((prev: any[]) => prev.filter((c) => c.id !== commentId));
  };

  const handleToggleComment = (lineIndex: number | null, side: 'old' | 'new') => {
    if (lineIndex === null) {
      setActiveCommentLine(null);
      setActiveSide(null);
    } else {
      setActiveCommentLine(lineIndex);
      setActiveSide(side);
    }
  };

  const handleToggleCommentWrapper = (lineIndex: number | null, side: 'old' | 'new') => {
    handleToggleComment(lineIndex, side);
  };

  if (!diff.trim()) {
    return (
      <div
        className={cn(
          "flex items-center justify-center h-full text-muted-foreground",
          className
        )}
      >
        <p className="text-sm">No changes detected</p>
      </div>
    );
  }

  return (
    <div className={cn("flex flex-col h-full overflow-hidden", className)}>
      {/* Header row */}
      <div className="flex shrink-0">
        <div className="flex-1 bg-red-50 dark:bg-red-950 px-3 py-1.5 border-b border-r text-sm font-medium text-red-900 dark:text-red-100">
          Original
        </div>
        <div className="flex-1 bg-green-50 dark:bg-green-950 px-3 py-1.5 border-b text-sm font-medium text-green-900 dark:text-green-100">
          Modified
        </div>
      </div>

      {/* Diff content */}
      <div className="flex-1 overflow-auto">
        <pre className="font-mono">
          {oldLines.map((oldLine, idx) => (
            <DiffLineRow
              key={idx}
              oldLine={oldLine}
              newLine={newLines[idx]}
              index={idx}
              comments={getCommentsForLine(idx)}
              isHovered={hoveredLine === idx}
              isCommentActive={activeCommentLine === idx}
              commentInput={activeCommentLine === idx ? commentInput : ""}
              activeSide={activeSide}
              onHover={setHoveredLine}
              onToggleComment={handleToggleComment}
              onCommentInputChange={setCommentInput}
              onAddComment={handleAddComment}
              onDeleteComment={handleDeleteComment}
            />
          ))}
        </pre>
      </div>
    </div>
  );
}
