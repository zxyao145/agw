// jsdom 没有排版机制。Radix tooltip 与大纲共用这个 ResizeObserver 接口，尺寸变化由 Electron 测试验证。
// jsdom has no layout engine. Radix tooltips and the outline use this ResizeObserver stub.
// Electron tests cover changes in layout size.
export class LayoutFreeResizeObserver {
  public observe(): void {}
  public unobserve(): void {}
  public disconnect(): void {}
}

/** 大纲各项的文字与 data-depth，无大纲时为 null。The outline items' text and data-depth, or null. */
export function outlineItems(container: ParentNode): [string | null, string | null][] | null {
  const nav = container.querySelector("nav.outline");
  if (!nav) return null;
  return [...nav.querySelectorAll(".outline-item")].map((item) => [
    item.textContent,
    item.getAttribute("data-depth"),
  ]);
}
