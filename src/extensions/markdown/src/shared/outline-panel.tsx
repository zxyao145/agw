import {
  useCallback,
  useEffect,
  useLayoutEffect,
  useRef,
  useState,
  type KeyboardEvent,
  type PointerEvent,
  type ReactElement,
} from "react";

import { activeHeadingIndex, type OutlineHeading } from "./outline";
import {
  outlineWidthRange,
  readOutlineSizes,
  showOutlineWidth,
  storeOutlineWidth,
} from "./outline-width";

type OutlinePanelProps = {
  headings: readonly OutlineHeading[];
  active: number | null;
  onSelect(index: number): void;
};

export function OutlinePanel({ headings, active, onSelect }: OutlinePanelProps): ReactElement {
  const scroll = useRef<HTMLDivElement>(null);
  const activeItem = useRef<HTMLButtonElement>(null);
  useLayoutEffect(() => {
    keepActiveHeadingVisible(scroll.current, activeItem.current);
  }, [active, headings]);

  const minLevel = headings.reduce((min, { level }) => Math.min(min, level), Infinity);
  return (
    <nav className="outline" aria-label="Outline">
      <OutlineResizer />
      <div ref={scroll} className="outline-scroll">
        <p className="outline-title">Outline</p>
        {headings.length === 0 ? (
          <p className="outline-empty">No headings</p>
        ) : (
          <ol className="outline-list">
            {headings.map(({ level, label }, index) => (
              <li key={index}>
                <button
                  ref={index === active ? activeItem : undefined}
                  type="button"
                  className="outline-item"
                  data-depth={level - minLevel}
                  aria-current={index === active ? "location" : undefined}
                  title={label}
                  onClick={() => onSelect(index)}
                >
                  {label}
                </button>
              </li>
            ))}
          </ol>
        )}
      </div>
    </nav>
  );
}

const KEYBOARD_STEP = 16;

function clampWidth(width: number, range: { min: number; max: number }): number {
  return Math.min(range.max, Math.max(range.min, width));
}

/**
 * 大纲左边缘的拖动手柄（Markdown 大纲宽度调整设计第 4 节）：拖动或方向键、Home、End 调整宽度，双击回到自动宽度。
 * 拖动中只修改 html 上的变量，结束时保存一次；没有移动的点击不保存。ARIA 数值直接写在元素上，不经过 React 提交。
 * The drag handle on the outline's left edge (Markdown outline width design, section 4): dragging or the arrow keys,
 * Home and End set the width, and a double click restores the automatic width. Dragging only changes the variable on
 * html and stores once at the end; a click without movement stores nothing. The ARIA values go straight onto the
 * element without a React commit.
 */
function OutlineResizer() {
  const handle = useRef<HTMLDivElement>(null);

  /** 大纲的当前宽度与用户宽度的范围。The outline's current width and the user width range. */
  const measure = () => {
    const nav = handle.current!.parentElement!;
    const layout = nav.parentElement!;
    return {
      width: Math.round(nav.getBoundingClientRect().width),
      range: outlineWidthRange(layout.clientWidth, readOutlineSizes(layout)),
    };
  };

  const describe = () => {
    const { width, range } = measure();
    const element = handle.current!;
    element.setAttribute("aria-valuemin", String(range.min));
    element.setAttribute("aria-valuemax", String(range.max));
    element.setAttribute("aria-valuenow", String(width));
  };

  const onPointerDown = (event: PointerEvent<HTMLDivElement>) => {
    if (event.button !== 0) return;
    event.preventDefault();
    const element = event.currentTarget;
    const root = document.documentElement;
    const { width: startWidth, range } = measure();
    const startX = event.clientX;
    let width: number | null = null;
    element.setPointerCapture(event.pointerId);
    root.setAttribute("data-outline-resizing", "");
    const onMove = (move: globalThis.PointerEvent) => {
      width = clampWidth(Math.round(startWidth + startX - move.clientX), range);
      showOutlineWidth(width);
      describe();
    };
    const onEnd = () => {
      element.removeEventListener("pointermove", onMove);
      element.removeEventListener("pointerup", onEnd);
      element.removeEventListener("pointercancel", onEnd);
      root.removeAttribute("data-outline-resizing");
      if (width !== null) storeOutlineWidth(width);
    };
    element.addEventListener("pointermove", onMove);
    element.addEventListener("pointerup", onEnd);
    element.addEventListener("pointercancel", onEnd);
  };

  const onKeyDown = (event: KeyboardEvent<HTMLDivElement>) => {
    const { width, range } = measure();
    const next =
      event.key === "ArrowLeft"
        ? width + KEYBOARD_STEP
        : event.key === "ArrowRight"
          ? width - KEYBOARD_STEP
          : event.key === "Home"
            ? range.min
            : event.key === "End"
              ? range.max
              : null;
    if (next === null) return;
    event.preventDefault();
    storeOutlineWidth(clampWidth(next, range));
    describe();
  };

  return (
    <div
      ref={handle}
      className="outline-resizer"
      role="separator"
      aria-orientation="vertical"
      aria-label="Resize Outline"
      tabIndex={0}
      title="Drag to resize. Double-click to reset."
      onPointerDown={onPointerDown}
      onKeyDown={onKeyDown}
      onDoubleClick={() => {
        storeOutlineWidth(null);
        describe();
      }}
      onFocus={describe}
    />
  );
}

function keepActiveHeadingVisible(panel: HTMLElement | null, item: HTMLButtonElement | null): void {
  if (!panel?.clientHeight || !item) return;
  const bounds = panel.getBoundingClientRect();
  const itemBounds = item.getBoundingClientRect();
  const { paddingTop, paddingBottom } = getComputedStyle(panel);
  const top = bounds.top + Number.parseFloat(paddingTop);
  const bottom = bounds.bottom - Number.parseFloat(paddingBottom);
  // 只滚动大纲，保留正文的阅读位置。Scroll the outline while keeping the document's reading position.
  if (itemBounds.top < top) panel.scrollTop += itemBounds.top - top;
  else if (itemBounds.bottom > bottom) panel.scrollTop += itemBounds.bottom - bottom;
}

type OutlineScroller = HTMLElement | Window;

type ActiveHeadingOptions = {
  scroller: OutlineScroller | null;
  documentColumn: HTMLElement | null;
  headingElements(): readonly (HTMLElement | null)[];
  headings: readonly OutlineHeading[];
};

export function useActiveHeading(options: ActiveHeadingOptions) {
  const { scroller, documentColumn, headingElements, headings } = options;
  const [active, setActive] = useState<number | null>(null);
  const pinnedScrollTop = useRef<number | null>(null);
  const readHeadingElements = useRef(headingElements);
  useLayoutEffect(() => {
    readHeadingElements.current = headingElements;
  }, [headingElements]);

  const measure = useCallback(() => {
    if (!scroller || !documentColumn) return;
    const viewport = scrollElement(scroller);
    if (pinnedScrollTop.current === viewport.scrollTop) return;
    pinnedScrollTop.current = null;
    setActive(measureActiveHeading(scroller, viewport, readHeadingElements.current()));
  }, [documentColumn, scroller]);

  useLayoutEffect(() => {
    pinnedScrollTop.current = null;
    measure();
  }, [headings, measure]);

  useEffect(() => {
    if (!scroller || !documentColumn) return;
    return observeHeadingLayout(scroller, documentColumn, measure);
  }, [documentColumn, measure, scroller]);

  return {
    active,
    pin(index: number): void {
      if (!scroller) throw new Error("Cannot pin a heading without a scroll area.");
      pinnedScrollTop.current = scrollElement(scroller).scrollTop;
      setActive(index);
    },
  };
}

function scrollElement(scroller: OutlineScroller): HTMLElement {
  return "document" in scroller ? scroller.document.documentElement : scroller;
}

function measureActiveHeading(
  scroller: OutlineScroller,
  viewport: HTMLElement,
  headingElements: readonly (HTMLElement | null)[],
): number | null {
  const { scrollTop, clientHeight, scrollHeight } = viewport;
  const top = viewport === scroller ? viewport.getBoundingClientRect().top : 0;
  const tops = headingElements.map((headingElement, index) => {
    if (!headingElement) throw new Error(`No heading element for outline item ${index}.`);
    return headingElement.getBoundingClientRect().top - top;
  });
  return activeHeadingIndex(tops, {
    height: clientHeight,
    atBottom: scrollTop + clientHeight >= scrollHeight - 1,
  });
}

function observeHeadingLayout(
  scroller: OutlineScroller,
  documentColumn: HTMLElement,
  measure: () => void,
): () => void {
  let frame: number | null = null;
  function schedule(): void {
    if (frame !== null) return;
    frame = requestAnimationFrame(() => {
      frame = null;
      measure();
    });
  }
  const observer = new ResizeObserver(schedule);
  observer.observe(documentColumn);
  scroller.addEventListener("scroll", schedule, { passive: true });
  // 窗口高度可以单独变化。The window height can change without resizing the content.
  window.addEventListener("resize", schedule);
  schedule();
  return () => {
    scroller.removeEventListener("scroll", schedule);
    window.removeEventListener("resize", schedule);
    observer.disconnect();
    if (frame !== null) cancelAnimationFrame(frame);
  };
}
