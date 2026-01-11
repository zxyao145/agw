"use client";

import * as React from "react";
import { cn } from "@/lib/utils";

interface DiffLine {
  type: "add" | "remove" | "context" | "header";
  oldLineNum?: number;
  newLineNum?: number;
  content: string;
}

interface DiffViewerProps {
  diff: string;
  className?: string;
}

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

export function DiffViewer({ diff, className }: DiffViewerProps) {
  const diffLines = React.useMemo(() => parseDiff(diff), [diff]);
  const { old: oldLines, new: newLines } = React.useMemo(
    () => splitDiffSides(diffLines),
    [diffLines]
  );

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
    <div className={cn("flex h-full overflow-hidden", className)}>
      {/* Left side - Old version (deletions) */}
      <div className="flex-1 flex flex-col border-r overflow-hidden">
        <div className="bg-red-50 dark:bg-red-950 px-3 py-1.5 border-b text-sm font-medium text-red-900 dark:text-red-100">
          Original
        </div>
        <div className="flex-1 overflow-auto">
          <pre className="text-xs font-mono">
            {oldLines.map((line, idx) => (
              <div
                key={idx}
                className={cn(
                  "flex",
                  line.type === "remove" && "bg-red-100 dark:bg-red-950/50",
                  line.type === "header" && "bg-muted/50 font-semibold",
                  line.content === "" && "min-h-[1.25rem]"
                )}
              >
                <span className="inline-block w-12 px-2 text-right text-muted-foreground select-none flex-shrink-0">
                  {line.oldLineNum}
                </span>
                <span className="px-2 flex-1 whitespace-pre-wrap break-all">
                  {line.content || " "}
                </span>
              </div>
            ))}
          </pre>
        </div>
      </div>

      {/* Right side - New version (additions) */}
      <div className="flex-1 flex flex-col overflow-hidden">
        <div className="bg-green-50 dark:bg-green-950 px-3 py-1.5 border-b text-sm font-medium text-green-900 dark:text-green-100">
          Modified
        </div>
        <div className="flex-1 overflow-auto">
          <pre className="text-xs font-mono">
            {newLines.map((line, idx) => (
              <div
                key={idx}
                className={cn(
                  "flex",
                  line.type === "add" && "bg-green-100 dark:bg-green-950/50",
                  line.type === "header" && "bg-muted/50 font-semibold",
                  line.content === "" && "min-h-[1.25rem]"
                )}
              >
                <span className="inline-block w-12 px-2 text-right text-muted-foreground select-none flex-shrink-0">
                  {line.newLineNum}
                </span>
                <span className="px-2 flex-1 whitespace-pre-wrap break-all">
                  {line.content || " "}
                </span>
              </div>
            ))}
          </pre>
        </div>
      </div>
    </div>
  );
}
