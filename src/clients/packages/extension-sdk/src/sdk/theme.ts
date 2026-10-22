import type { ThemeInfo } from "../api";

const TYPOGRAPHY_STYLESHEET = "/__agw/typography.css";

/**
 * 把主题变量与 dark class 写到 document 上，并只加入一次共享排版样式表。
 * Applies theme variables and the dark class to the document, and links the shared typography stylesheet once.
 */
export function applyTheme(theme: ThemeInfo, target: Document): void {
  if (!target.head.querySelector("link[data-agw-typography]")) {
    const link = target.createElement("link");
    link.rel = "stylesheet";
    link.href = TYPOGRAPHY_STYLESHEET;
    link.dataset.agwTypography = "";
    target.head.append(link);
  }
  const root = target.documentElement;
  for (const [name, value] of Object.entries(theme.tokens)) root.style.setProperty(name, value);
  root.classList.toggle("dark", theme.kind === "dark");
  root.style.colorScheme = theme.kind;
}
