import type { ExtensionApi } from "@agw/extension-sdk";
import type { Node as ProseMirrorNode } from "@tiptap/pm/model";
import type { Decoration, NodeView } from "@tiptap/pm/view";

import { createImageObjectUrl } from "../markdown/image-source";
import { classifyImage } from "../markdown/markdown-links";
import { errorMessage } from "../shared/errors";
import { referenceSource } from "./reference-decorations";

// 图片的 node view（设计 5.7）：显示规则与 Preview 相同。相对路径经 `fs.readFile` 生成 Blob URL，`data:` 地址直接显示，
// 其他地址显示 alt 与 "External image"。引用式图片的地址来自当前文档的定义（引用装饰）。不使用 React。
// The image node view (design section 5.7), with Preview's display rules: relative paths become Blob URLs through
// `fs.readFile`, `data:` addresses show directly, other addresses show the alt text and "External image". A reference
// image's address comes from the document's current definitions (the reference decorations). No React.

/** 显示的内容；`base` 为解析相对地址时所在的文件。What is shown; `base` is the file relative addresses resolve against. */
type Shown = { base: string; src: string | null; alt: string; title: string | null };

/** 图片显示的地址：引用式图片取装饰中解析的地址。The address an image shows; reference images take the resolved one. */
function imageSource(node: ProseMirrorNode, decorations: readonly Decoration[]): string | null {
  if (node.attrs.reference === null) return (node.attrs.src as string | null) ?? "";
  return referenceSource(decorations);
}

export class ImageView implements NodeView {
  public readonly dom: HTMLSpanElement;
  private node: ProseMirrorNode;
  private shown: Shown | null = null;
  private loading: AbortController | null = null;
  private objectUrl: string | null = null;

  public constructor(
    node: ProseMirrorNode,
    decorations: readonly Decoration[],
    private readonly api: ExtensionApi,
    private readonly fileUri: () => string,
  ) {
    this.node = node;
    this.dom = document.createElement("span");
    this.dom.className = "edit-image";
    this.show(node, decorations);
  }

  public update(node: ProseMirrorNode, decorations: readonly Decoration[]): boolean {
    if (node.type !== this.node.type) return false;
    this.node = node;
    this.show(node, decorations);
    return true;
  }

  public destroy(): void {
    this.release();
  }

  private show(node: ProseMirrorNode, decorations: readonly Decoration[]): void {
    const next: Shown = {
      base: this.fileUri(),
      src: imageSource(node, decorations),
      alt: String(node.attrs.alt ?? ""),
      title: (node.attrs.title as string | null) ?? null,
    };
    if (
      this.shown &&
      this.shown.base === next.base &&
      this.shown.src === next.src &&
      this.shown.alt === next.alt &&
      this.shown.title === next.title
    ) {
      return;
    }
    this.shown = next;
    this.release();
    if (next.src === null) {
      this.placeholder(next, "Missing link definition");
      return;
    }
    const source = classifyImage(next.base, next.src);
    if (source.kind === "data") this.image(source.src, next);
    else if (source.kind === "external") this.placeholder(next, "External image", next.src);
    else this.load(source.uri, next);
  }

  private load(uri: string, shown: Shown): void {
    this.placeholder(shown, "Loading image");
    const controller = new AbortController();
    this.loading = controller;
    createImageObjectUrl(this.api.fs, uri, controller.signal).then(
      (url) => {
        // 图片已经换掉或视图已经销毁时直接释放。Revoke at once when the image changed or the view is gone.
        if (controller.signal.aborted) {
          URL.revokeObjectURL(url);
          return;
        }
        this.objectUrl = url;
        this.image(url, shown);
      },
      (error: unknown) => {
        if (controller.signal.aborted) return;
        this.placeholder(shown, "Image unavailable", errorMessage(error));
      },
    );
  }

  private image(src: string, shown: Shown): void {
    const image = document.createElement("img");
    image.src = src;
    image.alt = shown.alt;
    if (shown.title !== null) image.title = shown.title;
    this.dom.replaceChildren(image);
  }

  private placeholder(shown: Shown, marker: string, title?: string): void {
    const placeholder = document.createElement("span");
    placeholder.className = "image-placeholder";
    if (title !== undefined) placeholder.title = title;
    if (shown.alt) {
      const alt = document.createElement("span");
      alt.className = "image-placeholder-alt";
      alt.textContent = shown.alt;
      placeholder.append(alt);
    }
    const label = document.createElement("span");
    label.className = "image-placeholder-marker";
    label.textContent = marker;
    placeholder.append(label);
    this.dom.replaceChildren(placeholder);
  }

  private release(): void {
    this.loading?.abort();
    this.loading = null;
    if (this.objectUrl !== null) URL.revokeObjectURL(this.objectUrl);
    this.objectUrl = null;
  }
}
