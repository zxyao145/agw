import * as React from "react";

export function useHorizontalTabWheel(ref: React.RefObject<HTMLElement | null>): void {
  React.useEffect(() => {
    const list = ref.current;
    if (!list) return;
    const scroll = (event: WheelEvent) => {
      const shouldScroll =
        !event.ctrlKey &&
        Math.abs(event.deltaY) > Math.abs(event.deltaX) &&
        list.scrollWidth > list.clientWidth;
      if (!shouldScroll) return;
      const unit =
        event.deltaMode === WheelEvent.DOM_DELTA_LINE
          ? 16
          : event.deltaMode === WheelEvent.DOM_DELTA_PAGE
            ? list.clientWidth
            : 1;
      event.preventDefault();
      list.scrollLeft += event.deltaY * unit;
    };
    list.addEventListener("wheel", scroll, { passive: false });
    return () => list.removeEventListener("wheel", scroll);
  }, [ref]);
}
