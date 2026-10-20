import type { ExtensionApi } from "@agw/extension-sdk";
import {
  createContext,
  use,
  useEffect,
  useState,
  type ComponentPropsWithoutRef,
  type MouseEvent,
} from "react";
import { createImageObjectUrl } from "../markdown/image-source";
import { classifyImage, classifyLink } from "../markdown/markdown-links";
import { errorMessage, notifyFailure } from "../shared/errors";
import type { MarkdownComponents, MarkdownImageProps } from "./markdown-pipeline";

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

export const markdownComponents: MarkdownComponents = { a: MarkdownLink, img: MarkdownImage };
