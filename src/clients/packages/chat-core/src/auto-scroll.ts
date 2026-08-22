export const CHAT_BOTTOM_TOLERANCE_PX = 24;

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

export function createAutoScrollState(): AutoScrollState {
  return { shouldAutoScroll: true, scrollHeight: 0, scrollTop: 0 };
}

export function updateAutoScrollState(
  state: AutoScrollState,
  metrics: ScrollMetrics,
  tolerance = CHAT_BOTTOM_TOLERANCE_PX,
): AutoScrollState {
  const isScrollingUp = metrics.scrollTop < state.scrollTop;
  const reachedCurrentBottom =
    metrics.scrollHeight - metrics.scrollTop - metrics.clientHeight <= tolerance;
  const reachedPreviousBottom =
    state.scrollHeight > 0 &&
    state.scrollHeight - metrics.scrollTop - metrics.clientHeight <= tolerance;

  return {
    shouldAutoScroll:
      !isScrollingUp && (state.shouldAutoScroll || reachedCurrentBottom || reachedPreviousBottom),
    scrollHeight: metrics.scrollHeight,
    scrollTop: metrics.scrollTop,
  };
}
