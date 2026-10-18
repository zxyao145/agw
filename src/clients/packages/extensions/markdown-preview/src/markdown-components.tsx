import type { ExtensionApi } from "@agw/extension-sdk";
import {
  createContext,
  use,
  useEffect,
  useState,
  type ComponentPropsWithoutRef,
  type MouseEvent,
} from "react";
import type { Components, ExtraProps } from "react-markdown";

import { errorMessage, notifyFailure } from "./errors";
import { classifyImage, classifyLink, imageMimeType } from "./markdown-links";

/** 当前渲染的文件与 API。The file being rendered and the API. */
export type MarkdownContextValue = { api: ExtensionApi; fileUri: string };

export const MarkdownContext = createContext<MarkdownContextValue | null>(null);

function useMarkdownContext(): MarkdownContextValue {
  const value = use(MarkdownContext);
  if (!value) throw new Error("MarkdownContext is missing.");
  return value;
}

function MarkdownLink({ node: _node, href, ...props }: ComponentPropsWithoutRef<"a"> & ExtraProps) {
  const { api, fileUri } = useMarkdownContext();
  const open = (event: MouseEvent<HTMLAnchorElement>) => {
    event.preventDefault();
    const target = classifyLink(fileUri, href ?? "");
    if (target.kind === "anchor") document.getElementById(target.id)?.scrollIntoView();
    else if (target.kind === "file") notifyFailure(api, api.workspace.openFile(target.uri));
    else if (target.kind === "external") notifyFailure(api, api.ui.openExternal(target.url));
  };
  return <a {...props} href={href} onClick={open} />;
}

function ImagePlaceholder({
  alt,
  marker,
  title,
}: {
  alt?: string;
  marker: string;
  title?: string;
}) {
  return (
    <span className="image-placeholder" title={title}>
      {alt ? <span className="image-placeholder-alt">{alt}</span> : null}
      <span className="image-placeholder-marker">{marker}</span>
    </span>
  );
}

type CloudImageState =
  | { status: "loading" }
  | { status: "loaded"; url: string }
  | { status: "error"; message: string };

/** 通过 `fs.readFile` 读取云端图片并用 Blob URL 显示。Reads a cloud image with `fs.readFile` and shows it through a Blob URL. */
function CloudImage({ uri, alt, title }: { uri: string; alt?: string; title?: string }) {
  const { api } = useMarkdownContext();
  const [state, setState] = useState<CloudImageState>({ status: "loading" });

  useEffect(() => {
    const controller = new AbortController();
    let url: string | null = null;
    api.fs.readFile(uri, { signal: controller.signal }).then(
      (data) => {
        if (controller.signal.aborted) return;
        // 结构化克隆得到的数据由 ArrayBuffer 承载。Structured-cloned data is backed by an ArrayBuffer.
        const bytes = data as Uint8Array<ArrayBuffer>;
        url = URL.createObjectURL(new Blob([bytes], { type: imageMimeType(uri) ?? "" }));
        setState({ status: "loaded", url });
      },
      (error: unknown) => {
        if (controller.signal.aborted) return;
        setState({ status: "error", message: errorMessage(error) });
      },
    );
    // 卸载（切换文件或视图卸载）时中止读取并释放 Blob URL。On unmount (file change or view unload), abort and revoke.
    return () => {
      controller.abort();
      if (url !== null) URL.revokeObjectURL(url);
    };
  }, [api, uri]);

  if (state.status === "loaded") return <img src={state.url} alt={alt} title={title} />;
  if (state.status === "error") {
    return <ImagePlaceholder alt={alt} marker="Image unavailable" title={state.message} />;
  }
  return <ImagePlaceholder alt={alt} marker="Loading image" title={title} />;
}

function MarkdownImage({ src, alt, title }: ComponentPropsWithoutRef<"img"> & ExtraProps) {
  const { fileUri } = useMarkdownContext();
  const raw = typeof src === "string" ? src : "";
  const source = classifyImage(fileUri, raw);
  if (source.kind === "data") return <img src={source.src} alt={alt} title={title} />;
  if (source.kind === "external") {
    return <ImagePlaceholder alt={alt} marker="External image" title={raw} />;
  }
  return <CloudImage key={source.uri} uri={source.uri} alt={alt} title={title} />;
}

export const markdownComponents: Components = { a: MarkdownLink, img: MarkdownImage };
