import type { AppearanceMode, AppearanceSettings, FontSizeSettings } from "./contracts";

/** 最小字号，单位 px，固定值。The minimum font size in px; a fixed value. */
export const MIN_FONT_SIZE = 14;

/** 宿主内置主题的 id。The id of the host's built-in theme. */
export const DEFAULT_THEME_ID = "default";

/** 不包含 `mode`：缺失的 `mode` 由 renderer 迁移。Has no `mode`; the renderer migrates a missing one. */
export const DEFAULT_APPEARANCE: AppearanceSettings = {
  baseFontSize: 16,
  chatFontSize: null,
  theme: DEFAULT_THEME_ID,
};

/** Reset all 恢复的 Mode。The mode Reset all restores. */
export const DEFAULT_MODE: AppearanceMode = "system";

export const APPEARANCE_MODES = [
  "system",
  "light",
  "dark",
] as const satisfies readonly AppearanceMode[];

/** `"{extensionId}:{themeId}"` 形式的主题 id。A theme id of the form `"{extensionId}:{themeId}"`. */
export function extensionThemeId(extensionId: string, themeId: string): string {
  return `${extensionId}:${themeId}`;
}

function range(from: number, to: number): readonly number[] {
  return Array.from({ length: to - from + 1 }, (_, index) => from + index);
}

export const BASE_FONT_SIZES: readonly number[] = range(MIN_FONT_SIZE, 20);
export const CHAT_FONT_SIZES: readonly number[] = range(MIN_FONT_SIZE, 24);

/** 各区域生效的字号，单位 px。The effective size of each area in px. */
export function resolveFontSizes(appearance: FontSizeSettings): {
  base: number;
  chat: number;
} {
  const { baseFontSize } = appearance;
  return {
    base: baseFontSize,
    chat: appearance.chatFontSize ?? Math.max(MIN_FONT_SIZE, baseFontSize * 0.875),
  };
}
