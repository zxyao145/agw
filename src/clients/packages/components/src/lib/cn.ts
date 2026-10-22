import { clsx, type ClassValue } from "clsx";
import { extendTailwindMerge } from "tailwind-merge";

// 主题字号 token（见 tokens.css 的 @theme static）需要登记为 font-size，否则 text-11 会被当作颜色。
// Register the theme font-size tokens (see @theme static in tokens.css) so text-11 is not treated as a color.
const twMerge = extendTailwindMerge({
  extend: { theme: { text: ["3xs", "2xs", "11", "13", "15"] } },
});

export function cn(...inputs: ClassValue[]) {
  return twMerge(clsx(inputs));
}
