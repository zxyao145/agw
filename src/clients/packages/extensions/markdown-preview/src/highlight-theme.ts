import type { ExtensionApi, ThemeInfo } from "@agw/extension-sdk";
import githubDark from "highlight.js/styles/github-dark.css?inline";
import githubLight from "highlight.js/styles/github.css?inline";

/**
 * 页面存续期间按宿主主题使用 highlight.js 的 GitHub 浅色或深色样式，主题变化时切换。
 * For the page's lifetime, uses highlight.js's GitHub light or dark styles for the host theme and switches on change.
 */
export function applyHighlightTheme(api: ExtensionApi): void {
  const style = document.createElement("style");
  document.head.append(style);
  const update = (theme: ThemeInfo) => {
    style.textContent = theme.kind === "dark" ? githubDark : githubLight;
  };
  update(api.theme.get());
  api.theme.onDidChange(update);
}
