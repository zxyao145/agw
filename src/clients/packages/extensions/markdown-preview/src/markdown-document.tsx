import type { ExtensionApi, FileViewerFile } from "@agw/extension-sdk";
import { useEffect, useMemo, useState } from "react";
import Markdown from "react-markdown";

import { errorMessage } from "./errors";
import { MarkdownContext, markdownComponents } from "./markdown-components";
import { rehypePlugins, remarkPlugins, urlTransform } from "./markdown-pipeline";

type DocumentState =
  | { status: "loading" }
  | { status: "loaded"; text: string }
  | { status: "error"; message: string };

/** 读取并渲染一个 Markdown 文件；文件变化时由调用方按 URI 重新挂载。Reads and renders one Markdown file; callers remount it per URI. */
export function MarkdownDocument({ api, file }: { api: ExtensionApi; file: FileViewerFile }) {
  const [state, setState] = useState<DocumentState>({ status: "loading" });
  const context = useMemo(() => ({ api, fileUri: file.uri }), [api, file.uri]);

  useEffect(() => {
    const controller = new AbortController();
    api.fs.readTextFile(file.uri, { signal: controller.signal }).then(
      (text) => {
        if (!controller.signal.aborted) setState({ status: "loaded", text });
      },
      (error: unknown) => {
        if (!controller.signal.aborted) setState({ status: "error", message: errorMessage(error) });
      },
    );
    return () => controller.abort();
  }, [api, file.uri]);

  if (state.status === "loading") return <p className="status">Loading {file.name}…</p>;
  if (state.status === "error") {
    return (
      <div className="status status-error" role="alert">
        <p className="status-title">Could not read {file.name}</p>
        <p>{state.message}</p>
      </div>
    );
  }
  return (
    <MarkdownContext value={context}>
      <article className="markdown-body">
        <Markdown
          remarkPlugins={remarkPlugins}
          rehypePlugins={rehypePlugins}
          urlTransform={urlTransform}
          components={markdownComponents}
        >
          {state.text}
        </Markdown>
      </article>
    </MarkdownContext>
  );
}
