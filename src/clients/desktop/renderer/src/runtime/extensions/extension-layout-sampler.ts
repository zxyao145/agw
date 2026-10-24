import type { ExtensionFrameBounds } from "@desktop/shared/contracts";

/** 占位区域在宿主内容坐标系中的矩形与有效可见性。A slot's rectangle in host content coordinates and its effective visibility. */
export type FrameLayout = { bounds: ExtensionFrameBounds; visible: boolean };

type Slot = { element: HTMLElement; last: FrameLayout | null };

const HIDDEN_BOUNDS: ExtensionFrameBounds = { x: 0, y: 0, width: 0, height: 0 };

function sameBounds(left: ExtensionFrameBounds, right: ExtensionFrameBounds): boolean {
  return (
    left.x === right.x &&
    left.y === right.y &&
    left.width === right.width &&
    left.height === right.height
  );
}

/**
 * 占位区域的有效可见性：已连接、有非空区域、`checkVisibility` 为 true（包括 visibility、有效透明度与 content-visibility），
 * 并且占位元素自身不是 `content-visibility: hidden`。
 * A slot's effective visibility: connected, with a non-empty area, `checkVisibility` true (visibility, effective
 * opacity and content-visibility included), and the slot itself not `content-visibility: hidden`.
 */
export function isSlotVisible(element: HTMLElement, rect: DOMRect): boolean {
  return (
    element.isConnected &&
    rect.width > 0 &&
    rect.height > 0 &&
    element.checkVisibility({
      visibilityProperty: true,
      opacityProperty: true,
      contentVisibilityAuto: true,
    }) &&
    getComputedStyle(element).getPropertyValue("content-visibility") !== "hidden"
  );
}

/**
 * 统一测量已挂载的 extension 占位区域，结果变化时才报告。`ResizeObserver`、窗口 resize、主题与 Mode 更新及字体加载调度下一帧的
 * 测量；连续模式（CSS 主题启用期间）每帧采样全部区域，包括暂时被 CSS 隐藏的区域，因此仅位置变化、`:hover`、`:has()`、
 * 过渡与动画也会更新。宿主 document 隐藏时暂停，重新显示时立即测量。
 * Measures the mounted extension slots in one place and reports only changed results. `ResizeObserver`, window
 * resizes, theme and mode updates and font loads schedule a measurement on the next frame; continuous mode (while a
 * CSS theme is active) samples every slot each frame, including slots CSS hides for a while, so position-only
 * changes, `:hover`, `:has()`, transitions and animations update too. Sampling pauses while the host document is
 * hidden and measures at once when it shows again.
 */
export class ExtensionLayoutSampler {
  private readonly slots = new Map<string, Slot>();
  private readonly resizeObserver: ResizeObserver;
  private continuous = false;
  private frame = 0;

  public constructor(
    private readonly document: Document,
    private readonly report: (frameId: string, layout: FrameLayout, element: HTMLElement) => void,
  ) {
    const view = document.defaultView!;
    this.resizeObserver = new view.ResizeObserver(() => this.schedule());
    view.addEventListener("resize", () => this.schedule());
    document.addEventListener("visibilitychange", () => this.onVisibilityChange());
    document.fonts.addEventListener("loadingdone", () => this.schedule());
  }

  /** 登记占位区域；返回的函数注销它并隐藏原生视图。Registers a slot; the returned function unregisters it and hides the native view. */
  public register(frameId: string, element: HTMLElement): () => void {
    const slot: Slot = { element, last: null };
    this.slots.set(frameId, slot);
    this.resizeObserver.observe(element);
    this.schedule();
    return () => {
      if (this.slots.get(frameId) !== slot) return;
      this.slots.delete(frameId);
      this.resizeObserver.unobserve(element);
      this.report(frameId, { bounds: HIDDEN_BOUNDS, visible: false }, element);
      if (this.slots.size === 0) this.cancel();
    };
  }

  /** CSS 主题启用期间连续采样。Samples continuously while a CSS theme is active. */
  public setContinuous(active: boolean): void {
    if (this.continuous === active) return;
    this.continuous = active;
    if (active) this.schedule();
  }

  /** 在下一帧测量，多个触发合并为一次。Measures on the next frame; several triggers merge into one. */
  public schedule(): void {
    if (this.frame !== 0 || this.slots.size === 0 || this.document.visibilityState === "hidden")
      return;
    this.frame = requestAnimationFrame(() => {
      this.frame = 0;
      this.measureAll();
      if (this.continuous) this.schedule();
    });
  }

  private onVisibilityChange(): void {
    if (this.document.visibilityState === "hidden") {
      this.cancel();
      return;
    }
    this.measureAll();
    if (this.continuous) this.schedule();
  }

  private cancel(): void {
    cancelAnimationFrame(this.frame);
    this.frame = 0;
  }

  private measureAll(): void {
    for (const [frameId, slot] of this.slots) {
      const rect = slot.element.getBoundingClientRect();
      const layout: FrameLayout = {
        bounds: { x: rect.left, y: rect.top, width: rect.width, height: rect.height },
        visible: isSlotVisible(slot.element, rect),
      };
      if (
        slot.last &&
        slot.last.visible === layout.visible &&
        sameBounds(slot.last.bounds, layout.bounds)
      )
        continue;
      slot.last = layout;
      this.report(frameId, layout, slot.element);
    }
  }
}
