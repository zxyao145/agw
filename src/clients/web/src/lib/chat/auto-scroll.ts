const BOTTOM_TOLERANCE_PX = 2;

export interface AutoScrollState {
  shouldAutoScroll: boolean;
  scrollHeight: number;
  scrollTop: number;
}

export interface ScrollMetrics {
  clientHeight: number;
  scrollHeight: number;
  scrollTop: number;
}

export function updateAutoScrollState(
  state: AutoScrollState,
  metrics: ScrollMetrics,
): AutoScrollState {
  const isScrollingUp = metrics.scrollTop < state.scrollTop;
  const reachedCurrentBottom =
    metrics.scrollHeight - metrics.scrollTop - metrics.clientHeight <= BOTTOM_TOLERANCE_PX;
  const reachedPreviousBottom =
    state.scrollHeight > 0 &&
    state.scrollHeight - metrics.scrollTop - metrics.clientHeight <= BOTTOM_TOLERANCE_PX;

  return {
    shouldAutoScroll:
      !isScrollingUp && (state.shouldAutoScroll || reachedCurrentBottom || reachedPreviousBottom),
    scrollHeight: metrics.scrollHeight,
    scrollTop: metrics.scrollTop,
  };
}
