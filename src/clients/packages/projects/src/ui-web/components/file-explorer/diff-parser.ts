import type { CodeViewerLine } from "./types";

export interface ParsedSplitDiff {
  original: CodeViewerLine[];
  modified: CodeViewerLine[];
}

const HUNK_HEADER_PATTERN = /^@@ -(\d+)(?:,\d+)? \+(\d+)(?:,\d+)? @@/;
const NO_NEWLINE_MARKER = "\\ No newline at end of file";

interface PendingChangeLine {
  line: CodeViewerLine;
  noNewline: boolean;
}

function placeholderLine(): CodeViewerLine {
  return {
    content: "",
    kind: "placeholder",
  };
}

function annotationLine(): CodeViewerLine {
  return {
    content: "No newline at end of file",
    kind: "annotation",
  };
}

export function parseUnifiedDiff(diffText: string): ParsedSplitDiff {
  const original: CodeViewerLine[] = [];
  const modified: CodeViewerLine[] = [];
  let originalLineNumber = 0;
  let modifiedLineNumber = 0;
  let insideHunk = false;
  let deletions: PendingChangeLine[] = [];
  let additions: PendingChangeLine[] = [];
  let lastPendingChange: PendingChangeLine | null = null;
  let lastLineWasContext = false;

  const flushChangeBlock = () => {
    const rowCount = Math.max(deletions.length, additions.length);
    for (let index = 0; index < rowCount; index += 1) {
      const deletion = deletions[index];
      const addition = additions[index];
      original.push(deletion?.line ?? placeholderLine());
      modified.push(addition?.line ?? placeholderLine());
    }

    for (let index = 0; index < rowCount; index += 1) {
      const deletion = deletions[index];
      const addition = additions[index];
      if (deletion?.noNewline || addition?.noNewline) {
        original.push(deletion?.noNewline ? annotationLine() : placeholderLine());
        modified.push(addition?.noNewline ? annotationLine() : placeholderLine());
      }
    }

    deletions = [];
    additions = [];
    lastPendingChange = null;
    lastLineWasContext = false;
  };

  for (const rawLine of diffText.split("\n")) {
    const line = rawLine.endsWith("\r") ? rawLine.slice(0, -1) : rawLine;
    if (line.startsWith("diff --git ")) {
      flushChangeBlock();
      insideHunk = false;
      continue;
    }

    const hunkMatch = line.match(HUNK_HEADER_PATTERN);
    if (hunkMatch) {
      flushChangeBlock();
      originalLineNumber = Number(hunkMatch[1]);
      modifiedLineNumber = Number(hunkMatch[2]);
      insideHunk = true;
      const hunkLine: CodeViewerLine = {
        content: line,
        kind: "hunk",
      };
      original.push(hunkLine);
      modified.push({ ...hunkLine });
      continue;
    }

    if (!insideHunk) continue;

    if (line === NO_NEWLINE_MARKER) {
      if (lastPendingChange) {
        lastPendingChange.noNewline = true;
      } else if (lastLineWasContext) {
        original.push(annotationLine());
        modified.push(annotationLine());
        lastLineWasContext = false;
      }
      continue;
    }

    const marker = line[0];
    const content = line.slice(1);
    if (marker === " ") {
      flushChangeBlock();
      original.push({ content, kind: "context", lineNumber: originalLineNumber });
      modified.push({ content, kind: "context", lineNumber: modifiedLineNumber });
      originalLineNumber += 1;
      modifiedLineNumber += 1;
      lastLineWasContext = true;
    } else if (marker === "-") {
      if (additions.length > 0) {
        flushChangeBlock();
      }
      const deletion: PendingChangeLine = {
        line: { content, kind: "deletion", lineNumber: originalLineNumber },
        noNewline: false,
      };
      deletions.push(deletion);
      lastPendingChange = deletion;
      lastLineWasContext = false;
      originalLineNumber += 1;
    } else if (marker === "+") {
      const addition: PendingChangeLine = {
        line: { content, kind: "addition", lineNumber: modifiedLineNumber },
        noNewline: false,
      };
      additions.push(addition);
      lastPendingChange = addition;
      lastLineWasContext = false;
      modifiedLineNumber += 1;
    }
  }

  flushChangeBlock();

  return { original, modified };
}
