/**
 * `@agw/components` 中经 Portal 渲染的内容与遮罩的 `data-slot`，以及 `@agw/chat` 的图片预览（`IMAGE_PREVIEW_SLOT`）。
 * 新增经 Portal 渲染的组件时同时加入这里与测试。
 * `data-slot`s of portaled content and overlays in `@agw/components`, plus the image preview of `@agw/chat`
 * (`IMAGE_PREVIEW_SLOT`); add new portaled components here and to the test.
 */
export const OVERLAY_SLOTS = [
  "image-preview",
  "dialog-content",
  "dialog-overlay",
  "alert-dialog-content",
  "alert-dialog-overlay",
  "sheet-content",
  "sheet-overlay",
  "drawer-content",
  "drawer-overlay",
  "popover-content",
  "dropdown-menu-content",
  "dropdown-menu-sub-content",
  "context-menu-content",
  "context-menu-sub-content",
  "select-content",
  "combobox-content",
  "tooltip-content",
] as const;

/** 覆盖整个窗口的遮罩。Overlays covering the whole window. */
const MODAL_SLOTS: ReadonlySet<string> = new Set([
  "image-preview",
  "dialog-overlay",
  "alert-dialog-overlay",
  "sheet-overlay",
  "drawer-overlay",
]);

export const OVERLAY_SELECTOR = [
  ...OVERLAY_SLOTS.map((slot) => `[data-slot="${slot}"]`),
  "[data-sonner-toast]",
].join(",");

export type OverlayArea = {
  left: number;
  top: number;
  right: number;
  bottom: number;
  modal: boolean;
  slot: string;
};

export type FrameArea = {
  frameId: string;
  left: number;
  top: number;
  right: number;
  bottom: number;
};

/** 在整个文档中查找打开且可见的弹出层。Finds open, visible overlays anywhere in the document. */
export function findOpenOverlays(document: Document): OverlayArea[] {
  const view = document.defaultView;
  if (!view) return [];
  return [...document.querySelectorAll<HTMLElement>(OVERLAY_SELECTOR)].flatMap((element) => {
    const rect = element.getBoundingClientRect();
    if (rect.width <= 0 || rect.height <= 0) return [];
    if (view.getComputedStyle(element).visibility === "hidden") return [];
    const slot = element.dataset.slot ?? "sonner-toast";
    return [
      {
        left: rect.left,
        top: rect.top,
        right: rect.right,
        bottom: rect.bottom,
        modal: MODAL_SLOTS.has(slot),
        slot,
      },
    ];
  });
}

function overlaps(left: OverlayArea, right: FrameArea): boolean {
  return (
    left.left < right.right &&
    left.right > right.left &&
    left.top < right.bottom &&
    left.bottom > right.top
  );
}

/**
 * 与弹出层重叠的 frame 需要换成截图；模态遮罩或 `obscureAll` 使全部可见 frame 都需要截图。
 * Frames overlapping an overlay need a snapshot; a modal overlay or `obscureAll` covers every visible frame.
 */
export function computeObscuredFrames(
  overlays: readonly OverlayArea[],
  frames: readonly FrameArea[],
  obscureAll = false,
): Set<string> {
  if (obscureAll || overlays.some((overlay) => overlay.modal)) {
    return new Set(frames.map((frame) => frame.frameId));
  }
  return new Set(
    frames
      .filter((frame) => overlays.some((overlay) => overlaps(overlay, frame)))
      .map((frame) => frame.frameId),
  );
}

/**
 * 观察 `document.body` 子树，按动画帧计算被宿主弹出层遮挡的 extension 视图。
 * Watches the `document.body` subtree and computes, per animation frame, which extension views host overlays cover.
 */
export class HostOverlayTracker {
  private readonly frameElements = new Map<string, HTMLElement>();
  private observer: MutationObserver | null = null;
  private scheduled = false;
  private obscureAll = false;
  private last = "";

  public constructor(
    private readonly document: Document,
    private readonly onChange: (frameIds: ReadonlySet<string>) => void,
  ) {}

  public start(): void {
    const view = this.document.defaultView;
    if (!view || this.observer) return;
    this.observer = new view.MutationObserver(() => this.schedule());
    this.observer.observe(this.document.body, { childList: true, subtree: true, attributes: true });
    this.schedule();
  }

  public stop(): void {
    this.observer?.disconnect();
    this.observer = null;
  }

  /** 视图占位元素；null 表示该 frame 不再显示。The view slot element; null when the frame is no longer shown. */
  public setFrameElement(frameId: string, element: HTMLElement | null): void {
    if (element) this.frameElements.set(frameId, element);
    else this.frameElements.delete(frameId);
    this.schedule();
  }

  /** 重连对话框不经 Portal 渲染，由外壳直接通知。The reconnect dialog is not portaled, so the shell reports it. */
  public setObscureAll(obscureAll: boolean): void {
    this.obscureAll = obscureAll;
    this.schedule();
  }

  public schedule(): void {
    const view = this.document.defaultView;
    if (this.scheduled || !view) return;
    this.scheduled = true;
    view.requestAnimationFrame(() => {
      this.scheduled = false;
      this.compute();
    });
  }

  private compute(): void {
    const overlays = findOpenOverlays(this.document);
    const frames = [...this.frameElements].flatMap(([frameId, element]): FrameArea[] => {
      const rect = element.getBoundingClientRect();
      if (rect.width <= 0 || rect.height <= 0) return [];
      return [{ frameId, left: rect.left, top: rect.top, right: rect.right, bottom: rect.bottom }];
    });
    const obscured = computeObscuredFrames(overlays, frames, this.obscureAll);
    const key = [...obscured].sort().join(",");
    if (key !== this.last) {
      this.last = key;
      this.onChange(obscured);
    }
    // 弹出层打开期间每个动画帧重新计算（位置可能在动画中变化）。Recompute every frame while overlays are open.
    if (overlays.length > 0) this.schedule();
  }
}
