"use client";

import * as React from "react";
import { cn } from "@agw/components";
import { parseUnifiedDiff } from "./diff-parser";
import FileViewer from "./file-viewer";
import { CommentSide, DiffViewerProps, LineComment } from "./types";

const DEFAULT_SPLIT_PERCENTAGE = 50;
const MIN_SPLIT_PERCENTAGE = 20;
const MAX_SPLIT_PERCENTAGE = 80;
const KEYBOARD_RESIZE_STEP = 2;
const SCROLL_SYNC_TOLERANCE = 1;

interface ScrollPosition {
  top: number;
  left: number;
}

function clampSplitPercentage(value: number): number {
  return Math.min(MAX_SPLIT_PERCENTAGE, Math.max(MIN_SPLIT_PERCENTAGE, value));
}

function mapScrollOffset(offset: number, sourceRange: number, targetRange: number): number {
  if (sourceRange <= 0 || targetRange <= 0) return 0;
  return Math.min(targetRange, Math.max(0, (offset / sourceRange) * targetRange));
}

function hasScrollPosition(element: HTMLDivElement, position: ScrollPosition): boolean {
  return (
    Math.abs(element.scrollTop - position.top) <= SCROLL_SYNC_TOLERANCE &&
    Math.abs(element.scrollLeft - position.left) <= SCROLL_SYNC_TOLERANCE
  );
}

export function DiffViewer({
  diff,
  className,
  filePath = "",
  comments = [],
  setComments,
  scope,
}: DiffViewerProps) {
  const { original: originalLines, modified: modifiedLines } = React.useMemo(
    () => parseUnifiedDiff(diff),
    [diff],
  );
  const [originalLabel, modifiedLabel] =
    scope === "staged"
      ? ["HEAD", "Staged"]
      : scope === "unstaged"
        ? ["Staged", "Working Tree"]
        : ["Original", "Modified"];
  const containerRef = React.useRef<HTMLDivElement>(null);
  const originalScrollRef = React.useRef<HTMLDivElement>(null);
  const modifiedScrollRef = React.useRef<HTMLDivElement>(null);
  const programmaticScrollPositionsRef = React.useRef(
    new WeakMap<HTMLDivElement, ScrollPosition>(),
  );
  const [splitPercentage, setSplitPercentage] = React.useState(DEFAULT_SPLIT_PERCENTAGE);
  const [isResizing, setIsResizing] = React.useState(false);
  const hasRenderableDiffLines = React.useMemo(
    () =>
      originalLines.some((line) => line.kind !== "hunk" && line.kind !== "placeholder") ||
      modifiedLines.some((line) => line.kind !== "hunk" && line.kind !== "placeholder"),
    [modifiedLines, originalLines],
  );

  const isOriginalComment = React.useCallback(
    (comment: LineComment) =>
      comment.filePath === filePath &&
      comment.side === CommentSide.Original &&
      comment.diffScope === scope,
    [filePath, scope],
  );

  const isModifiedComment = React.useCallback(
    (comment: LineComment) =>
      comment.filePath === filePath &&
      comment.side === CommentSide.Modified &&
      comment.diffScope === scope,
    [filePath, scope],
  );

  const originalComments = React.useMemo(
    () => comments.filter(isOriginalComment),
    [comments, isOriginalComment],
  );

  const modifiedComments = React.useMemo(
    () => comments.filter(isModifiedComment),
    [comments, isModifiedComment],
  );

  // FileViewer updates one side at a time, so merge the edited slice back into the full list.
  const handleSetOriginalComments = React.useCallback(
    (setter: React.SetStateAction<LineComment[]>) => {
      if (!setComments) return;
      setComments((prev) => {
        const currentOriginalComments = prev.filter(isOriginalComment);
        const otherComments = prev.filter((comment) => !isOriginalComment(comment));

        const newOriginalComments =
          typeof setter === "function" ? setter(currentOriginalComments) : setter;

        return [...otherComments, ...newOriginalComments];
      });
    },
    [isOriginalComment, setComments],
  );

  const handleSetModifiedComments = React.useCallback(
    (setter: React.SetStateAction<LineComment[]>) => {
      if (!setComments) return;
      setComments((prev) => {
        const currentModifiedComments = prev.filter(isModifiedComment);
        const otherComments = prev.filter((comment) => !isModifiedComment(comment));

        const newModifiedComments =
          typeof setter === "function" ? setter(currentModifiedComments) : setter;

        return [...otherComments, ...newModifiedComments];
      });
    },
    [isModifiedComment, setComments],
  );

  const updateSplitPosition = React.useCallback((clientX: number) => {
    const containerRect = containerRef.current?.getBoundingClientRect();
    if (!containerRect || containerRect.width === 0) return;

    const nextPercentage = ((clientX - containerRect.left) / containerRect.width) * 100;
    setSplitPercentage(clampSplitPercentage(nextPercentage));
  }, []);

  const handlePointerDown = (event: React.PointerEvent<HTMLDivElement>) => {
    event.preventDefault();
    event.currentTarget.setPointerCapture(event.pointerId);
    setIsResizing(true);
    updateSplitPosition(event.clientX);
  };

  const handlePointerMove = (event: React.PointerEvent<HTMLDivElement>) => {
    if (!isResizing) return;

    event.preventDefault();
    updateSplitPosition(event.clientX);
  };

  const handlePointerEnd = (event: React.PointerEvent<HTMLDivElement>) => {
    if (event.currentTarget.hasPointerCapture(event.pointerId)) {
      event.currentTarget.releasePointerCapture(event.pointerId);
    }
    setIsResizing(false);
  };

  const handleSeparatorKeyDown = (event: React.KeyboardEvent<HTMLDivElement>) => {
    if (event.key !== "ArrowLeft" && event.key !== "ArrowRight") return;

    event.preventDefault();
    const direction = event.key === "ArrowLeft" ? -1 : 1;
    setSplitPercentage((current) =>
      clampSplitPercentage(current + direction * KEYBOARD_RESIZE_STEP),
    );
  };

  const syncScroll = React.useCallback(
    (event: React.UIEvent<HTMLDivElement>, targetRef: React.RefObject<HTMLDivElement | null>) => {
      const source = event.currentTarget;
      const programmedPosition = programmaticScrollPositionsRef.current.get(source);
      if (programmedPosition) {
        programmaticScrollPositionsRef.current.delete(source);
        if (hasScrollPosition(source, programmedPosition)) return;
      }

      const target = targetRef.current;
      if (!target) return;

      const nextPosition = {
        top: mapScrollOffset(
          source.scrollTop,
          source.scrollHeight - source.clientHeight,
          target.scrollHeight - target.clientHeight,
        ),
        left: mapScrollOffset(
          source.scrollLeft,
          source.scrollWidth - source.clientWidth,
          target.scrollWidth - target.clientWidth,
        ),
      };
      if (hasScrollPosition(target, nextPosition)) return;

      programmaticScrollPositionsRef.current.set(target, nextPosition);
      target.scrollTop = nextPosition.top;
      target.scrollLeft = nextPosition.left;
    },
    [],
  );

  React.useEffect(() => {
    if (!isResizing) return;

    const previousCursor = document.body.style.cursor;
    const previousUserSelect = document.body.style.userSelect;
    document.body.style.cursor = "col-resize";
    document.body.style.userSelect = "none";

    return () => {
      document.body.style.cursor = previousCursor;
      document.body.style.userSelect = previousUserSelect;
    };
  }, [isResizing]);

  if (!diff.trim()) {
    return (
      <div
        className={cn("flex items-center justify-center h-full text-muted-foreground", className)}
      >
        <p className="text-sm">No changes detected</p>
      </div>
    );
  }

  if (!hasRenderableDiffLines) {
    return (
      <div
        className={cn("h-full overflow-auto bg-muted/10 agw-scrollbar", className)}
        aria-label="Git diff metadata"
      >
        <pre className="min-w-max p-4 font-mono text-sm whitespace-pre text-muted-foreground">
          {diff.trimEnd()}
        </pre>
      </div>
    );
  }

  return (
    <div
      ref={containerRef}
      className={cn("grid h-full min-w-0 overflow-hidden", className)}
      style={{
        gridTemplateColumns: `calc(${splitPercentage}% - 2px) 4px minmax(0, 1fr)`,
        gridTemplateRows: "auto minmax(0, 1fr)",
      }}
    >
      <div className="col-start-1 row-start-1 min-w-0 border-b border-border bg-red-50 px-3 py-1.5 text-sm font-medium text-red-900 dark:bg-red-950 dark:text-red-100">
        {originalLabel}
      </div>

      <div
        role="separator"
        aria-label="Resize diff panels"
        aria-orientation="vertical"
        aria-valuemin={MIN_SPLIT_PERCENTAGE}
        aria-valuemax={MAX_SPLIT_PERCENTAGE}
        aria-valuenow={Math.round(splitPercentage)}
        tabIndex={0}
        className={cn(
          "group relative z-10 col-start-2 row-span-2 row-start-1 touch-none cursor-col-resize outline-none",
          "focus-visible:ring-2 focus-visible:ring-primary/40",
        )}
        onPointerDown={handlePointerDown}
        onPointerMove={handlePointerMove}
        onPointerUp={handlePointerEnd}
        onPointerCancel={handlePointerEnd}
        onLostPointerCapture={() => setIsResizing(false)}
        onKeyDown={handleSeparatorKeyDown}
      >
        <div
          className={cn(
            "pointer-events-none absolute inset-y-0 left-1/2 w-px -translate-x-1/2 bg-border transition-colors",
            "group-hover:bg-primary/60 group-focus-visible:bg-primary/60",
            isResizing && "bg-primary",
          )}
        />
      </div>

      <div className="col-start-3 row-start-1 min-w-0 border-b border-border bg-green-50 px-3 py-1.5 text-sm font-medium text-green-900 dark:bg-green-950 dark:text-green-100">
        {modifiedLabel}
      </div>

      <div
        ref={originalScrollRef}
        className="col-start-1 row-start-2 min-h-0 min-w-0 overflow-auto agw-scrollbar"
        onScroll={(event) => syncScroll(event, modifiedScrollRef)}
      >
        <FileViewer
          content=""
          lines={originalLines}
          filePath={filePath}
          comments={originalComments}
          setComments={handleSetOriginalComments}
          isDiffView={true}
          commentSide={CommentSide.Original}
          diffScope={scope}
        />
      </div>

      <div
        ref={modifiedScrollRef}
        className="col-start-3 row-start-2 min-h-0 min-w-0 overflow-auto agw-scrollbar"
        onScroll={(event) => syncScroll(event, originalScrollRef)}
      >
        <FileViewer
          content=""
          lines={modifiedLines}
          filePath={filePath}
          comments={modifiedComments}
          setComments={handleSetModifiedComments}
          isDiffView={true}
          commentSide={CommentSide.Modified}
          diffScope={scope}
        />
      </div>
    </div>
  );
}
