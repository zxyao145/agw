const OUTLINE_LEVELS = [1, 2, 3, 4] as const;

/** 大纲中的标题。A heading in the outline. */
export type OutlineHeading = { level: (typeof OUTLINE_LEVELS)[number]; label: string };

export function isOutlineLevel(level: unknown): level is OutlineHeading["level"] {
  return OUTLINE_LEVELS.some((allowedLevel) => allowedLevel === level);
}

/** 合并连续空白并去掉首尾空白。Collapses runs of whitespace and trims. */
export function outlineLabel(text: string): string {
  return text.replace(/\s+/gu, " ").trim();
}

/** 两个大纲的每一项 level 与 label 都相同。Both outlines have equal level and label at every item. */
export function sameOutline(a: readonly OutlineHeading[], b: readonly OutlineHeading[]): boolean {
  return (
    a.length === b.length &&
    a.every(({ level, label }, index) => {
      const heading = b[index]!;
      return heading.level === level && heading.label === label;
    })
  );
}

// 16px 的 scroll-margin-top 加 8px。The 16px scroll-margin-top plus 8px.
const ACTIVE_HEADING_TOP = 24;

/**
 * tops[i] 为第 i 个标题上沿相对滚动区域可见部分上沿的距离，height 为可见部分高度，单位为 px。
 * atBottom 表示滚动区域已经滚动到底部。
 * tops[i] is heading i's top relative to the visible part of the scroller, and height is that part's height, in px.
 * atBottom indicates that the scroller has reached the end.
 */
export function activeHeadingIndex(
  tops: readonly number[],
  { height, atBottom }: { height: number; atBottom: boolean },
): number | null {
  const index = tops.findLastIndex((top) => (atBottom ? top < height : top <= ACTIVE_HEADING_TOP));
  return index === -1 ? null : index;
}
