import type { Disposable, ExtensionApi } from "@agw/extension-sdk";

import { errorMessage, notifyFailure } from "./errors";
import { renderMermaid } from "./mermaid-render";

// Mermaid 代码块的图表（Mermaid 设计第 1 节），Edit 的 node view 与 Preview 共用。图表渲染出来后右上角有预览按钮，预览
// 由宿主在 Desktop 窗口上显示（第 4 节）。不使用 React。
// Diagrams for Mermaid code blocks (Mermaid design section 1), shared by Edit's node view and Preview. A rendered
// diagram has a preview button in its top-right corner, and the host shows the preview over the Desktop window
// (section 4). No React.

const SVG_NS = "http://www.w3.org/2000/svg";
/** 预览图片四周的留白。The padding around a preview image. */
const PREVIEW_PADDING = 16;
/** 预览图片的名称：替代文字与另存时的默认文件名。The preview image's name: its alt text and default file name. */
export const PREVIEW_IMAGE_NAME = "mermaid-diagram.svg";
// Lucide maximize-2 图标（ISC 许可）。The Lucide maximize-2 icon (ISC license).
const PREVIEW_ICON =
  '<path d="M15 3h6v6"/><path d="m21 3-7 7"/><path d="m3 21 7-7"/><path d="M9 21H3v-6"/>';

/**
 * 图表的预览图片：独立的 SVG 文件，按原始大小，四周留白并铺上 `background`。宿主预览的背景为深色，浅色主题的图表需要
 * 自己的背景才能看清。
 * The diagram's preview image: a standalone SVG file at natural size, padded and laid on `background`. The host's
 * preview backdrop is dark, so a light-theme diagram needs its own background to stay readable.
 */
export function diagramImage(svg: SVGSVGElement, background: string): Uint8Array<ArrayBuffer> {
  const { x, y, width, height } = svg.viewBox.baseVal;
  const box = {
    x: x - PREVIEW_PADDING,
    y: y - PREVIEW_PADDING,
    width: width + PREVIEW_PADDING * 2,
    height: height + PREVIEW_PADDING * 2,
  };
  const image = svg.cloneNode(true) as SVGSVGElement;
  image.setAttribute("viewBox", `${box.x} ${box.y} ${box.width} ${box.height}`);
  image.setAttribute("width", String(box.width));
  image.setAttribute("height", String(box.height));
  image.style.removeProperty("max-width");
  // HTML 解析把 foreignObject 中元素的 xmlns 留作普通属性，序列化时它会与元素的命名空间声明重复。HTML parsing leaves
  // the xmlns of elements in foreignObject as plain attributes, which would duplicate their namespace declarations.
  for (const element of image.querySelectorAll("[xmlns]")) element.removeAttributeNS(null, "xmlns");
  const backdrop = document.createElementNS(SVG_NS, "rect");
  for (const [name, value] of Object.entries({ ...box, fill: background })) {
    backdrop.setAttribute(name, String(value));
  }
  image.prepend(backdrop);
  return new TextEncoder().encode(new XMLSerializer().serializeToString(image));
}

function previewButton(): HTMLButtonElement {
  const button = document.createElement("button");
  button.type = "button";
  button.className = "mermaid-diagram-open";
  button.title = "Preview diagram";
  button.setAttribute("aria-label", "Preview diagram");
  const icon = document.createElementNS(SVG_NS, "svg");
  const attributes = {
    viewBox: "0 0 24 24",
    width: "16",
    height: "16",
    fill: "none",
    stroke: "currentColor",
    "stroke-width": "2",
    "stroke-linecap": "round",
    "stroke-linejoin": "round",
    "aria-hidden": "true",
  };
  for (const [name, value] of Object.entries(attributes)) icon.setAttribute(name, value);
  icon.innerHTML = PREVIEW_ICON;
  button.append(icon);
  return button;
}

/**
 * `empty`：源码为空；`pending`：还没有渲染结果；`rendered`：显示图表；`failed`：显示错误信息。
 * `empty`: the source is blank; `pending`: no render result yet; `rendered`: a diagram shows; `failed`: an error shows.
 */
export type MermaidDiagramState = "empty" | "pending" | "rendered" | "failed";

/**
 * 一个图表的显示。重新渲染期间保留上一次的结果；渲染期间的修改合并为结束后的一次渲染。主题变化时重新渲染。
 * One diagram's display. A re-render keeps the previous result until it finishes; changes during a render merge into
 * one render after it. A theme change renders again.
 */
export class MermaidDiagram {
  public readonly dom: HTMLDivElement;
  private readonly openButton: HTMLButtonElement;
  private currentState: MermaidDiagramState = "empty";
  private source = "";
  private rendering = false;
  /** 渲染期间源码或主题有变化，结果作废。The source or theme changed during the render, so its result is dropped. */
  private stale = false;
  private destroyed = false;
  private readonly themeSubscription: Disposable;

  /** `onStateChange` 在状态变化时调用。`onStateChange` runs whenever the state changes. */
  public constructor(
    private readonly api: ExtensionApi,
    private readonly onStateChange: () => void,
  ) {
    this.dom = document.createElement("div");
    this.dom.className = "mermaid-diagram";
    this.dom.contentEditable = "false";
    this.openButton = previewButton();
    // 按钮自己处理按下与点击，使用方对图表区域的处理不会收到它们。The button handles its own press and click, so the
    // owner's handling of the diagram area does not receive them.
    this.openButton.addEventListener("mousedown", (event) => {
      event.preventDefault();
      event.stopPropagation();
    });
    this.openButton.addEventListener("click", (event) => {
      event.stopPropagation();
      notifyFailure(api, this.openPreview());
    });
    this.themeSubscription = api.theme.onDidChange(() => this.schedule());
  }

  public get state(): MermaidDiagramState {
    return this.currentState;
  }

  public update(source: string): void {
    if (source === this.source) return;
    this.source = source;
    if (!source.trim()) {
      this.stale = this.rendering;
      this.dom.replaceChildren();
      this.setState("empty");
      return;
    }
    if (this.currentState === "empty") this.setState("pending");
    this.schedule();
  }

  /**
   * 请宿主在 Desktop 窗口上预览当前图表，背景为主题的 `--background`；用户关闭预览时完成。只用于已经渲染出来的图表。
   * Asks the host to preview the current diagram over the Desktop window, on the theme's `--background`; settles when
   * the user closes the preview. Only for a rendered diagram.
   */
  public openPreview(): Promise<void> {
    const svg = this.dom.querySelector<SVGSVGElement>(":scope > svg");
    if (this.currentState !== "rendered" || svg === null) {
      throw new Error("The Mermaid diagram has not rendered.");
    }
    const background = getComputedStyle(document.documentElement)
      .getPropertyValue("--background")
      .trim();
    if (!background) throw new Error("The theme has no --background color.");
    return this.api.ui.previewImage({
      data: diagramImage(svg, background),
      type: "image/svg+xml",
      name: PREVIEW_IMAGE_NAME,
    });
  }

  public destroy(): void {
    this.destroyed = true;
    this.themeSubscription.dispose();
  }

  private schedule(): void {
    if (this.rendering) {
      this.stale = true;
      return;
    }
    if (!this.source.trim()) return;
    void this.run();
  }

  private async run(): Promise<void> {
    this.rendering = true;
    let source = this.source;
    for (;;) {
      this.stale = false;
      let svg: string | null = null;
      let failure = "";
      try {
        svg = await renderMermaid(source, this.api.theme.get());
      } catch (error) {
        // 源码错误是用户的内容，显示给用户。Source errors are the user's content and are shown to the user.
        failure = errorMessage(error);
      }
      if (this.destroyed) return;
      if (!this.stale) {
        if (svg === null) this.showError(failure);
        else this.showSvg(svg);
        break;
      }
      source = this.source;
      // 渲染期间源码改为空：update 已经清空图表。The source became blank during the render; update cleared it.
      if (!source.trim()) break;
    }
    this.rendering = false;
  }

  private showSvg(svg: string): void {
    // mermaid 的 strict 级别已用 DOMPurify 清理 SVG。mermaid's strict level has sanitized the SVG with DOMPurify.
    this.dom.innerHTML = svg;
    this.dom.append(this.openButton);
    this.setState("rendered");
  }

  private showError(message: string): void {
    const error = document.createElement("div");
    error.className = "mermaid-diagram-error";
    error.textContent = message;
    this.dom.replaceChildren(error);
    this.setState("failed");
  }

  private setState(state: MermaidDiagramState): void {
    if (state === this.currentState) return;
    this.currentState = state;
    this.onStateChange();
  }
}
