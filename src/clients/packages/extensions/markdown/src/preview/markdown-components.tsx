import type { ExtensionApi } from "@agw/extension-sdk";
import {
  createContext,
  use,
  useEffect,
  useRef,
  useState,
  type ComponentPropsWithoutRef,
  type MouseEvent,
} from "react";
import { createImageObjectUrl } from "../markdown/image-source";
import { classifyImage, classifyLink } from "../markdown/markdown-links";
import { errorMessage, notifyFailure } from "../shared/errors";
import { MermaidDiagram, type MermaidDiagramState } from "../shared/mermaid-diagram";
import type {
  MarkdownComponents,
  MarkdownImageProps,
  MermaidBlockProps,
} from "./markdown-pipeline";

/** 当前渲染的文件与 API。The file being rendered and the API. */
export type MarkdownContextValue = { api: ExtensionApi; fileUri: string };

export const MarkdownContext = createContext<MarkdownContextValue | null>(null);

function useMarkdownContext(): MarkdownContextValue {
  const value = use(MarkdownContext);
  if (!value) throw new Error("MarkdownContext is missing.");
  return value;
}

function MarkdownLink({ href, ...props }: ComponentPropsWithoutRef<"a">) {
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

/** 读取云端图片并用 Blob URL 显示。Reads a cloud image and shows it through a Blob URL. */
function CloudImage({ uri, alt, title }: { uri: string; alt?: string; title?: string }) {
  const { api } = useMarkdownContext();
  const [state, setState] = useState<CloudImageState>({ status: "loading" });

  useEffect(() => {
    const controller = new AbortController();
    let url: string | null = null;
    createImageObjectUrl(api.fs, uri, controller.signal).then(
      (created) => {
        // URL 生成之后才卸载时直接释放。Revoke at once when the unmount came after the URL was created.
        if (controller.signal.aborted) {
          URL.revokeObjectURL(created);
          return;
        }
        url = created;
        setState({ status: "loaded", url: created });
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

function MarkdownImage({ src, alt, title }: MarkdownImageProps) {
  const { fileUri } = useMarkdownContext();
  const raw = src ?? "";
  const source = classifyImage(fileUri, raw);
  if (source.kind === "data") return <img src={source.src} alt={alt} title={title} />;
  if (source.kind === "external") {
    return <ImagePlaceholder alt={alt} marker="External image" title={raw} />;
  }
  return <CloudImage key={source.uri} uri={source.uri} alt={alt} title={title} />;
}

/**
 * Mermaid 代码块（Mermaid 设计第 5 节）：图表渲染出来后隐藏源码，点击图表由宿主在 Desktop 窗口上预览；源码有错误时同时
 * 显示源码与错误信息。源码变化时保留当前图表直到新的结果出来。
 * A Mermaid code block (Mermaid design section 5): the source hides once the diagram renders, and clicking the diagram
 * has the host preview it over the Desktop window; a source error shows the source with the error. When the source
 * changes, the current diagram stays until the new result arrives.
 */
function MermaidBlock({ source, children }: MermaidBlockProps) {
  const { api } = useMarkdownContext();
  const container = useRef<HTMLDivElement>(null);
  const diagram = useRef<MermaidDiagram | null>(null);
  const [state, setState] = useState<MermaidDiagramState>(source.trim() ? "pending" : "empty");

  useEffect(() => {
    const created = new MermaidDiagram(api, () => setState(created.state));
    container.current!.append(created.dom);
    diagram.current = created;
    return () => {
      created.destroy();
      created.dom.remove();
      diagram.current = null;
    };
  }, [api]);

  useEffect(() => {
    diagram.current!.update(source);
    setState(diagram.current!.state);
  }, [api, source]);

  const open = () => {
    if (diagram.current?.state === "rendered") notifyFailure(api, diagram.current.openPreview());
  };

  return (
    <div className="mermaid-block" data-diagram={state}>
      {children}
      <div ref={container} onClick={open} />
    </div>
  );
}

export const markdownComponents: MarkdownComponents = {
  a: MarkdownLink,
  img: MarkdownImage,
  mermaid: MermaidBlock,
};
