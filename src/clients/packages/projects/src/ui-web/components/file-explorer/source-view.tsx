import * as React from "react";
import { CodeViewer } from "@agw/code-view";

import type { EditableSource, SourceDocumentState } from "./source-document";
import { SourceDocumentError } from "./source-document-error";
import { SourceEncodingMenu } from "./source-encoding-menu";
import { CommentSide, type LineComment } from "./types";
import { useLineCommentZones } from "./use-line-comment-zones";

interface SourceViewProps {
  filePath: string;
  /** Web 的只读文字。Web's read-only text. */
  content: string;
  /** Desktop 的可编辑文档；没有时只读。Desktop's editable document; read-only without it. */
  editable?: EditableSource;
  wordWrap: boolean;
  /** 编辑器能格式化时为执行格式化的函数，否则为 null。A function that formats when the editor can, otherwise null. */
  onFormatChange(format: (() => void) | null): void;
  comments: LineComment[];
  setComments: React.Dispatch<React.SetStateAction<LineComment[]>>;
}

/**
 * Source（设计文档第 3、5 节）：Monaco 编辑器、状态栏与行评论。Web 中只读；Desktop 中编辑宿主的文档，读取中与失败时
 * 显示说明，焦点在其中时 `Cmd/Ctrl+S` 保存。
 * Source (design sections 3 and 5): the Monaco editor, the status bar and line comments. Read-only in Web; in Desktop
 * it edits the host's document, shows explanations while loading or failed, and saves on `Cmd/Ctrl+S` while focus is
 * inside.
 */
export function SourceView({
  filePath,
  content,
  editable,
  wordWrap,
  onFormatChange,
  comments,
  setComments,
}: SourceViewProps) {
  const zones = useLineCommentZones({
    comments,
    setComments,
    filePath,
    side: CommentSide.Current,
    isDiffView: false,
  });
  // Source 显示了这个文件：宿主创建文档或刷新。Source shows this file: the host creates the document or refreshes it.
  const show = React.useEffectEvent(() => editable?.show());
  React.useEffect(() => show(), [filePath]);

  if (editable === undefined) {
    return (
      <CodeViewer
        content={{ kind: "text", text: content }}
        filePath={filePath}
        ariaLabel={`Source of ${filePath}`}
        wordWrap={wordWrap}
        {...zones.side}
      />
    );
  }
  const { document } = editable;
  if (document === null) return <SourceStatus text={`Loading ${baseName(filePath)}…`} />;
  return (
    <EditableSourceView
      document={document}
      filePath={filePath}
      wordWrap={wordWrap}
      onFormatChange={onFormatChange}
      zones={zones}
    />
  );
}

function baseName(filePath: string): string {
  return filePath.split(/[\\/]/u).pop() ?? filePath;
}

function SourceStatus({ text }: { text: string }) {
  return <p className="m-0 px-8 py-6 text-[0.875em] text-muted-foreground">{text}</p>;
}

function EditableSourceView({
  document,
  filePath,
  wordWrap,
  onFormatChange,
  zones,
}: {
  document: SourceDocumentState;
  filePath: string;
  wordWrap: boolean;
  onFormatChange(format: (() => void) | null): void;
  zones: ReturnType<typeof useLineCommentZones>;
}) {
  const { status, error } = document;
  const shown = status !== "loading" && status !== "failed";
  return (
    <div
      className="flex h-full min-h-0 flex-col"
      onKeyDownCapture={(event) => {
        if (!document.isSaveShortcut(event)) return;
        event.preventDefault();
        document.save();
      }}
    >
      {status === "loading" ? <SourceStatus text={`Loading ${document.fileName}…`} /> : null}
      {status === "failed" && error !== null ? (
        <SourceDocumentError document={document} error={error} />
      ) : null}
      {/* 读取中与失败时编辑器隐藏但保持挂载。The editor hides but stays mounted while loading or failed. */}
      <div className="min-h-0 flex-1" hidden={!shown}>
        <CodeViewer
          content={{
            kind: "editable",
            model: document.model,
            shown,
            encoding: <SourceEncodingMenu document={document} />,
            onFormatChange,
            onError: (message) => document.notifyError(message),
            onDidChangeLines: zones.onDidChangeLines,
          }}
          filePath={filePath}
          ariaLabel={`Source of ${filePath}`}
          wordWrap={wordWrap}
          {...zones.side}
        />
      </div>
    </div>
  );
}
