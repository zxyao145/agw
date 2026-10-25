import type { AppearanceMode, AppearanceSettings, FontSizeSettings } from "./contracts";

/** 宿主内置主题的 id。The id of the host's built-in theme. */
export const DEFAULT_THEME_ID = "default";

/** 不包含 `mode`：缺失的 `mode` 由 renderer 迁移。Has no `mode`; the renderer migrates a missing one. */
export const DEFAULT_APPEARANCE: AppearanceSettings = {
  baseFontSize: 16,
  chatFontSize: null,
  codeFontSize: null,
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

export const BASE_FONT_SIZES: readonly number[] = range(12, 20);
export const CHAT_FONT_SIZES: readonly number[] = range(12, 24);
export const CODE_FONT_SIZES: readonly number[] = range(12, 28);

/**
 * 各区域生效的字号，单位 px。Code 默认为基础字号的 0.875 倍，按 0.5px 四舍五入。
 * The effective size of each area in px. Code defaults to 0.875 times the base size, rounded to 0.5px.
 */
export function resolveFontSizes(appearance: FontSizeSettings): {
  base: number;
  chat: number;
  code: number;
} {
  const { baseFontSize } = appearance;
  return {
    base: baseFontSize,
    chat: appearance.chatFontSize ?? baseFontSize * 0.875,
    code: appearance.codeFontSize ?? Math.round(baseFontSize * 0.875 * 2) / 2,
  };
}
