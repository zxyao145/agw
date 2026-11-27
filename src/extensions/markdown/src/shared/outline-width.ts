// 用户调整的大纲宽度（Markdown 大纲宽度调整设计第 3 节）。Mixed 与 Preview 的页面同源，共用 localStorage；宽度写在 html
// 上，outline.css 据此改变网格的大纲列与大纲宽度。
// The outline width the user sets (Markdown outline width design, section 3). Mixed and Preview pages share an origin
// and so localStorage; the width goes on html, and outline.css changes the grid's outline column and the outline width
// from it.

export const OUTLINE_WIDTH_KEY = "markdown.outlineWidth";

export type OutlineSizes = { min: number; gutter: number; documentMin: number };

function cssPixels(style: CSSStyleDeclaration, name: string): number {
  const value = style.getPropertyValue(name).trim();
  const match = /^(\d+(?:\.\d+)?)px$/u.exec(value);
  if (match === null) throw new Error(`${name} is not a px length: "${value}".`);
  return Number(match[1]);
}

/** 从元素的计算样式读取大纲尺寸（px）。Reads the outline sizes in px from the element's computed style. */
export function readOutlineSizes(element: Element): OutlineSizes {
  const style = getComputedStyle(element);
  return {
    min: cssPixels(style, "--outline-min-width"),
    gutter: cssPixels(style, "--outline-gutter"),
    documentMin: cssPixels(style, "--document-min-width"),
  };
}

/** 用户宽度的范围：内容列至少保留 documentMin。The user width range, keeping at least documentMin for the content. */
export function outlineWidthRange(
  layoutWidth: number,
  { min, gutter, documentMin }: OutlineSizes,
): { min: number; max: number } {
  return { min, max: Math.max(min, layoutWidth - gutter - documentMin) };
}

/**
 * localStorage 中的用户宽度，没有时为 null。这个值只由 storeOutlineWidth 写入，不是正整数时抛出；最小宽度由 CSS 保证。
 * The stored user width, or null. Only storeOutlineWidth writes it, so anything but a positive integer throws; CSS
 * keeps the minimum width.
 */
export function readStoredOutlineWidth(): number | null {
  const value = localStorage.getItem(OUTLINE_WIDTH_KEY);
  if (value === null) return null;
  if (!/^[1-9]\d*$/u.test(value)) {
    throw new Error(`The stored outline width is invalid: "${value}".`);
  }
  return Number(value);
}

/** 在 html 上显示用户宽度，null 时回到自动宽度；不写存储。Shows the user width on html, or the automatic width for null. */
export function showOutlineWidth(width: number | null): void {
  const root = document.documentElement;
  if (width === null) {
    root.removeAttribute("data-outline-width");
    root.style.removeProperty("--outline-user-width");
    return;
  }
  root.setAttribute("data-outline-width", "");
  root.style.setProperty("--outline-user-width", `${width}px`);
}

/** 保存并显示用户宽度；null 时删除存储并回到自动宽度。Stores and shows the user width; null removes it. */
export function storeOutlineWidth(width: number | null): void {
  if (width === null) localStorage.removeItem(OUTLINE_WIDTH_KEY);
  else localStorage.setItem(OUTLINE_WIDTH_KEY, String(width));
  showOutlineWidth(width);
}

/**
 * 页面存续期间：显示存储中的宽度，另一个页面修改或清空存储时更新。
 * For the page's lifetime: shows the stored width and follows another page changing or clearing the storage.
 */
export function applyOutlineWidth(): void {
  showOutlineWidth(readStoredOutlineWidth());
  window.addEventListener("storage", (event) => {
    if (event.key === OUTLINE_WIDTH_KEY || event.key === null) {
      showOutlineWidth(readStoredOutlineWidth());
    }
  });
}
