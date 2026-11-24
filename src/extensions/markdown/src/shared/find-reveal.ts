// 当前匹配是否需要滚动（Markdown 搜索替换设计第 4 节）。Whether the current match needs scrolling (Markdown find and
// replace design, section 4).

type Area = { top: number; bottom: number; left: number; right: number };

function outside(match: DOMRect, area: Area): boolean {
  return (
    match.top < area.top ||
    match.bottom > area.bottom ||
    match.left < area.left ||
    match.right > area.right
  );
}

/**
 * 从 `from` 向上到 `until`（不含）之间可以横向滚动、但没有完整显示 `match` 的元素，例如代码块与表格。
 * The horizontally scrollable elements from `from` up to `until` (exclusive) that do not show `match` in full, such as
 * code blocks and tables.
 */
export function clippingScrollers(match: DOMRect, from: Element, until: Element): Element[] {
  const scrollers: Element[] = [];
  for (
    let element: Element | null = from;
    element && element !== until;
    element = element.parentElement
  ) {
    if (element.scrollWidth <= element.clientWidth) continue;
    if (getComputedStyle(element).overflowX === "visible") continue;
    const box = element.getBoundingClientRect();
    if (match.left < box.left || match.right > box.right) scrollers.push(element);
  }
  return scrollers;
}

/** 与搜索栏相交；搜索栏关闭时为 false。Whether it intersects the find bar; false while the bar is closed. */
export function coveredByFindBar(match: DOMRect): boolean {
  const bar = document.querySelector(".find-bar")?.getBoundingClientRect();
  if (!bar) return false;
  return (
    match.left < bar.right &&
    match.right > bar.left &&
    match.top < bar.bottom &&
    match.bottom > bar.top
  );
}

/**
 * 匹配不在 `scroller`（null 为窗口）的可见区域内、被横向滚动的祖先元素遮住一部分，或与搜索栏相交时返回 true。
 * True when the match is outside the visible area of `scroller` (null for the window), partly hidden by a horizontally
 * scrolling ancestor, or intersecting the find bar.
 */
export function needsReveal(match: DOMRect, from: Element, scroller: Element | null): boolean {
  const area = scroller
    ? scroller.getBoundingClientRect()
    : { top: 0, left: 0, bottom: window.innerHeight, right: window.innerWidth };
  return (
    outside(match, area) ||
    clippingScrollers(match, from, scroller ?? document.body).length > 0 ||
    coveredByFindBar(match)
  );
}
