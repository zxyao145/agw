import {
  useCallback,
  useEffect,
  useLayoutEffect,
  useRef,
  useState,
  type ReactElement,
} from "react";

import { activeHeadingIndex, type OutlineHeading } from "./outline";

type OutlinePanelProps = {
  headings: readonly OutlineHeading[];
  active: number | null;
  onSelect(index: number): void;
};

export function OutlinePanel({ headings, active, onSelect }: OutlinePanelProps): ReactElement {
  const nav = useRef<HTMLElement>(null);
  const activeItem = useRef<HTMLButtonElement>(null);
  useLayoutEffect(() => {
    keepActiveHeadingVisible(nav.current, activeItem.current);
  }, [active, headings]);

  const minLevel = headings.reduce((min, { level }) => Math.min(min, level), Infinity);
  return (
    <nav ref={nav} className="outline" aria-label="Outline">
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
    </nav>
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
