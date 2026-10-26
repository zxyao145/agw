import * as React from "react";
import { type CodeViewSideProps, type LineChange, type LineZone, remapLine } from "@agw/code-view";

import type { GitDiffScope } from "../../../services/files";
import { CommentSection } from "./comment-section";
import {
  createLineComment,
  groupCommentsByLine,
  type LineCommentTarget,
  remapCommentLines,
} from "./line-comments";
import { useProjectFileScope } from "./project-file-scope";
import type { CommentSide, LineComment } from "./types";

const ADD_COMMENT = "Add comment";
/** 正在新增评论、还没有评论的行区域的 key。The key of the zone getting a new comment on a line without comments. */
const NEW_COMMENT_KEY = "new";

interface LineCommentZonesOptions {
  comments: LineComment[];
  setComments: React.Dispatch<React.SetStateAction<LineComment[]>>;
  filePath: string;
  side: CommentSide;
  diffScope?: GitDiffScope;
  isDiffView: boolean;
}

export interface LineCommentZones {
  side: CodeViewSideProps;
  /** 可编辑的编辑器内容变化后移动评论与正在新增评论的行。Moves comments and the new comment line after edits. */
  onDidChangeLines(changes: readonly LineChange[]): void;
}

/**
 * 一个编辑器中的行评论（设计文档第 6、7 节）：有评论的行与正在新增评论的行各有一个行区域，行号旁的 "Add comment" 只在
 * 没有评论的行可用。行区域的 key 为该行第一条评论的 id，行号随编辑变化时评论框保留状态。
 * Line comments in one editor (design sections 6 and 7): lines with comments and the line getting a new comment each
 * have a zone, and "Add comment" beside the line number works only on lines without comments. A zone's key is the id
 * of its line's first comment, so comment boxes keep their state while edits move the line.
 */
export function useLineCommentZones({
  comments,
  setComments,
  filePath,
  side,
  diffScope,
  isDiffView,
}: LineCommentZonesOptions): LineCommentZones {
  const { projectId, directoryId, directoryName } = useProjectFileScope();
  const target = React.useMemo<LineCommentTarget>(
    () => ({ scope: { projectId, directoryId, directoryName }, filePath, side, diffScope }),
    [projectId, directoryId, directoryName, filePath, side, diffScope],
  );
  const [activeLine, setActiveLine] = React.useState<number | null>(null);
  const byLine = React.useMemo(() => groupCommentsByLine(comments, target), [comments, target]);

  const lineZones = React.useMemo<LineZone[]>(() => {
    const zones = [...byLine].map(([line, group]) => ({
      key: `comment:${group[0].id}`,
      afterLineNumber: line,
    }));
    if (activeLine !== null && !byLine.has(activeLine)) {
      zones.push({ key: NEW_COMMENT_KEY, afterLineNumber: activeLine });
    }
    return zones.sort((left, right) => left.afterLineNumber - right.afterLineNumber);
  }, [byLine, activeLine]);
  const markedLines = React.useMemo(() => [...byLine.keys()], [byLine]);
  const lineZoneAction = React.useMemo(
    () => ({
      label: ADD_COMMENT,
      canRun: (line: number) => !byLine.has(line),
      run: (line: number) => setActiveLine((current) => (current === line ? null : line)),
    }),
    [byLine],
  );

  const onDidChangeLines = React.useCallback(
    (changes: readonly LineChange[]) => {
      setComments((current) => remapCommentLines(current, target, changes));
      setActiveLine((current) => (current === null ? null : remapLine(current, changes)));
    },
    [setComments, target],
  );

  const renderLineZone = (zone: LineZone) => {
    const line = zone.afterLineNumber;
    return (
      <div className="relative border-y border-border bg-muted/50" data-comment-line={line}>
        {/* 左侧强调条。The accent bar on the left. */}
        <div className="absolute bottom-0 left-0 top-0 w-1 bg-blue-500" />
        <CommentSection
          lineComments={[...(byLine.get(line) ?? [])]}
          isCommentActive={activeLine === line}
          onAddComment={(content) => {
            if (!content.trim()) return;
            const id = `${Date.now()}-${Math.random().toString(36).slice(2, 11)}`;
            setComments((current) => [
              ...current,
              createLineComment(target, line, content, id, new Date()),
            ]);
            setActiveLine(null);
          }}
          onDeleteComment={(id) =>
            setComments((current) => current.filter((item) => item.id !== id))
          }
          onUpdateComment={(id, content) =>
            setComments((current) =>
              current.map((item) => (item.id === id ? { ...item, content } : item)),
            )
          }
          onCancelNewComment={() => setActiveLine(null)}
          isDiffView={isDiffView}
          commentSide={side}
        />
      </div>
    );
  };

  return { side: { lineZones, markedLines, lineZoneAction, renderLineZone }, onDidChangeLines };
}
