import type { AppearanceSettings } from "./contracts";

/** 最小字号，单位 px，固定值。The minimum font size in px; a fixed value. */
export const MIN_FONT_SIZE = 14;

export const DEFAULT_APPEARANCE: AppearanceSettings = {
  baseFontSize: 16,
  chatFontSize: null,
};

function range(from: number, to: number): readonly number[] {
  return Array.from({ length: to - from + 1 }, (_, index) => from + index);
}

export const BASE_FONT_SIZES: readonly number[] = range(MIN_FONT_SIZE, 20);
export const CHAT_FONT_SIZES: readonly number[] = range(MIN_FONT_SIZE, 24);

/** 各区域生效的字号，单位 px。The effective size of each area in px. */
export function resolveFontSizes(appearance: AppearanceSettings): {
  base: number;
  chat: number;
} {
  const { baseFontSize } = appearance;
  return {
    base: baseFontSize,
    chat: appearance.chatFontSize ?? Math.max(MIN_FONT_SIZE, baseFontSize * 0.875),
  };
}
