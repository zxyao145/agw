import type { ExtensionApi, FileViewerFile } from "@agw/extension-sdk";
import { useEffect, useMemo, useState } from "react";

import { DocumentError } from "../shared/document-error";
import { MarkdownContext, markdownComponents } from "./markdown-components";
import { renderMarkdown } from "./markdown-pipeline";

type DocumentState =
  | { status: "loading" }
  | { status: "loaded"; text: string }
  | { status: "error"; error: unknown };

/**
 * 读取并渲染一个 Markdown 文件；`shown` 每次变化都重新读取（同一个文件也读取），读到之前保留当前内容。
 * 文件变化时由调用方按 URI 重新挂载。
 * Reads and renders one Markdown file, reading it again whenever `shown` changes (same file included) while the
 * current content stays. Callers remount it per URI.
 */
export function MarkdownDocument({
  api,
  file,
  shown,
}: {
  api: ExtensionApi;
  file: FileViewerFile;
  shown: number;
}) {
  const [state, setState] = useState<DocumentState>({ status: "loading" });
  // 用户为这个文件选择的编码，null 时识别编码；每次选择都是新的对象，再次选择同一个编码也重新读取。
  // The encoding the user chose for this file, null to detect it; each choice is a new object, so choosing the same
  // encoding again reads again.
  const [choice, setChoice] = useState<{ encoding: string } | null>(null);
  const context = useMemo(() => ({ api, fileUri: file.uri }), [api, file.uri]);

  useEffect(() => {
    const controller = new AbortController();
    api.fs
      .readTextDocument(file.uri, { encoding: choice?.encoding, signal: controller.signal })
      .then(
        (textDocument) => {
          if (!controller.signal.aborted) setState({ status: "loaded", text: textDocument.text });
        },
        (error: unknown) => {
          if (!controller.signal.aborted) setState({ status: "error", error });
        },
      );
    return () => controller.abort();
  }, [api, file.uri, choice, shown]);

  const text = state.status === "loaded" ? state.text : null;
  const content = useMemo(
    () => (text === null ? null : renderMarkdown(text, markdownComponents)),
    [text],
  );

  if (state.status === "loading") return <p className="status">Loading {file.name}…</p>;
  if (state.status === "error") {
    return (
      <DocumentError
        fileName={file.name}
        error={state.error}
        listEncodings={() => api.fs.listTextEncodings()}
        onReopenWithEncoding={(encoding) => {
          setState({ status: "loading" });
          setChoice({ encoding });
        }}
      />
    );
  }
  return (
    <MarkdownContext value={context}>
      <article className="markdown-body agw-prose" data-density="document">
        {content}
      </article>
    </MarkdownContext>
  );
}
