// 把当前匹配滚动到可见位置（Markdown 搜索替换设计第 4 节）。Scrolls the current match into view (Markdown find and
// replace design, section 4).

type Area = { top: number; bottom: number; left: number; right: number };

function areaOf(scroller: Element | null): Area {
  return scroller
    ? scroller.getBoundingClientRect()
    : { top: 0, left: 0, bottom: window.innerHeight, right: window.innerWidth };
}

/**
 * 从 `from` 向上到 `until`（不含）之间可以横向滚动、但没有完整显示 `match` 的元素，例如代码块与表格。
 * The horizontally scrollable elements from `from` up to `until` (exclusive) that do not show `match` in full, such as
 * code blocks and tables.
 */
function clippingScrollers(match: DOMRect, from: Element, until: Element): Element[] {
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
function coveredByFindBar(match: DOMRect): boolean {
  const bar = document.querySelector(".find-bar")?.getBoundingClientRect();
  if (!bar) return false;
  return (
    match.left < bar.right &&
    match.right > bar.left &&
    match.top < bar.bottom &&
    match.bottom > bar.top
  );
}

/** 竖直方向不在可见区域内，或与搜索栏相交。Vertically outside the visible area, or intersecting the find bar. */
function hiddenVertically(match: DOMRect, area: Area): boolean {
  return match.top < area.top || match.bottom > area.bottom || coveredByFindBar(match);
}

/**
 * 当前匹配被横向滚动的祖先元素遮住时，让这些元素把它移到中部；之后它在 `scroller`（null 为窗口）中竖直方向不可见或
 * 与搜索栏相交时，把它移到竖直中部。匹配完整显示时什么都不做，重复调用不会再次滚动。`rect` 每次读取匹配的当前位置。
 * When horizontally scrolling ancestors hide the current match, they move it to their middle; when it is then
 * vertically outside `scroller` (null for the window) or under the find bar, it moves to the vertical middle. A match
 * shown in full is left alone, so calling again scrolls no more. `rect` reads the match's current position each time.
 */
export function revealMatch(rect: () => DOMRect, from: Element, scroller: Element | null): void {
  for (const element of clippingScrollers(rect(), from, scroller ?? document.body)) {
    const box = element.getBoundingClientRect();
    const match = rect();
    element.scrollLeft += (match.left + match.right) / 2 - (box.left + box.right) / 2;
  }
  const area = areaOf(scroller);
  const match = rect();
  if (!hiddenVertically(match, area)) return;
  const offset = (match.top + match.bottom) / 2 - (area.top + area.bottom) / 2;
  if (scroller) scroller.scrollTop += offset;
  else window.scrollBy({ top: offset });
}
