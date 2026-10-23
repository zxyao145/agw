import { resolveFontSizes } from "@desktop/shared/appearance";
import type { FontSizeSettings } from "@desktop/shared/contracts";

/** 主题字号中小于 1rem 的 token 及其基准值。Theme font size tokens below 1rem and their base values. */
const FLOORED_TEXT_TOKENS = {
  "--text-3xs": "0.5625rem",
  "--text-2xs": "0.625rem",
  "--text-xs": "0.75rem",
  "--text-11": "0.6875rem",
  "--text-sm": "0.875rem",
  "--text-13": "0.8125rem",
  "--text-15": "0.9375rem",
} as const;

/**
 * 把字号设置解析为写入根元素的 CSS 变量，小于 1rem 的主题字号不低于最小字号。
 * Resolves the appearance into CSS custom properties for the root element; theme sizes below 1rem respect the minimum.
 */
export function resolveAppearance(appearance: FontSizeSettings): Record<string, string> {
  const sizes = resolveFontSizes(appearance);
  return {
    "--agw-font-size-base": `${sizes.base}px`,
    "--agw-font-size-chat": `${sizes.chat}px`,
    ...Object.fromEntries(
      Object.entries(FLOORED_TEXT_TOKENS).map(([token, value]) => [
        token,
        `max(var(--agw-font-size-min), ${value})`,
      ]),
    ),
  };
}
