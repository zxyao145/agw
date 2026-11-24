import type { ExtensionApi, FileViewerFile } from "@agw/extension-sdk";
import { useEffect, useLayoutEffect, useMemo, useRef, useState } from "react";

import { DocumentError } from "../shared/document-error";
import { FindBar, type FindBarMemory } from "../shared/find-bar";
import type { Platform } from "../shared/find-query";
import { OutlinePanel, useActiveHeading } from "../shared/outline-panel";
import { MarkdownContext, markdownComponents } from "./markdown-components";
import { indexRenderedHeadings, renderMarkdown } from "./markdown-pipeline";
import { PreviewSearch } from "./preview-search";

type DocumentState =
  | { status: "loading" }
  | { status: "loaded"; text: string }
  | { status: "error"; error: unknown };

/**
 * 读取并渲染一个 Markdown 文件；`shown` 每次变化都重新读取（同一个文件也读取），读到之前保留当前内容。
 * 文件变化时由调用方按 URI 重新挂载，搜索栏的状态由页面持有的 `findMemory` 恢复。
 * Reads and renders one Markdown file, reading it again whenever `shown` changes (same file included) while the
 * current content stays. Callers remount it per URI, and the page-owned `findMemory` restores the find bar.
 */
export function MarkdownDocument({
  api,
  file,
  shown,
  findMemory,
}: {
  api: ExtensionApi;
  file: FileViewerFile;
  shown: number;
  findMemory: FindBarMemory;
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
      <MarkdownDocumentLayout
        text={state.text}
        findMemory={findMemory}
        platform={api.context.get().app.platform}
      />
    </MarkdownContext>
  );
}

function MarkdownDocumentLayout({
  text,
  findMemory,
  platform,
}: {
  text: string;
  findMemory: FindBarMemory;
  platform: Platform;
}) {
  const { element, outline: headings } = useMemo(
    () => renderMarkdown(text, markdownComponents),
    [text],
  );
  const [documentColumn, setDocumentColumn] = useState<HTMLElement | null>(null);
  const [search, setSearch] = useState<PreviewSearch | null>(null);
  useEffect(() => {
    if (!documentColumn) return;
    const created = new PreviewSearch(documentColumn);
    setSearch(created);
    return () => created.dispose();
  }, [documentColumn]);
  const headingElementsById = useRef<ReadonlyMap<string, HTMLElement>>(new Map());
  useLayoutEffect(() => {
    if (documentColumn) headingElementsById.current = indexRenderedHeadings(documentColumn);
  }, [documentColumn, headings]);
  const { active, pin } = useActiveHeading({
    scroller: window,
    documentColumn,
    headingElements: () => headings.map(({ id }) => headingElementsById.current.get(id) ?? null),
    headings,
  });

  function selectHeading(index: number): void {
    headingElementsById.current.get(headings[index]!.id)!.scrollIntoView({ block: "start" });
    pin(index);
  }

  return (
    <div
      className="preview-layout outline-layout"
      data-outline={headings.length > 0 ? "" : undefined}
    >
      {search ? <FindBar target={search} memory={findMemory} platform={platform} enabled /> : null}
      <article
        ref={setDocumentColumn}
        className="markdown-body agw-prose document-column"
        data-density="document"
      >
        {element}
      </article>
      {headings.length > 0 ? (
        <OutlinePanel headings={headings} active={active} onSelect={selectHeading} />
      ) : null}
    </div>
  );
}
