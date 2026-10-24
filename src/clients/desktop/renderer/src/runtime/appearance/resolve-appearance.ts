import { resolveFontSizes } from "@desktop/shared/appearance";
import type { FontSizeSettings } from "@desktop/shared/contracts";

/**
 * 把字号设置解析为写入根元素的 CSS 变量。
 * Resolves the appearance into CSS custom properties for the root element.
 */
export function resolveAppearance(appearance: FontSizeSettings): Record<string, string> {
  const sizes = resolveFontSizes(appearance);
  return {
    "--agw-font-size-base": `${sizes.base}px`,
    "--agw-font-size-chat": `${sizes.chat}px`,
  };
}
