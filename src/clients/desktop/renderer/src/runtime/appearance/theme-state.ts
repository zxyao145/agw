import type { ThemeInfo } from "@agw/extension-sdk";
import type { ThemeStyleText } from "@desktop/shared/contracts";
import { THEME_TOKEN_NAMES } from "@desktop/shared/theme-tokens";

/**
 * 发布给 extension 的主题：公共变量的计算值、页面背景色，以及 CSS 主题的样式文本（JSON 主题与 Default 为 null）。
 * The theme published to extensions: the computed public variables, the page background, and the CSS theme style
 * text (null for JSON themes and Default).
 */
export type PublishedTheme = {
  theme: Omit<ThemeInfo, "cssText">;
  background: string;
  style: ThemeStyleText | null;
};

/**
 * 读取推送给 extension 的主题：正在使用的主题的 id 与名称，以及变量的计算值，其中的 `var()` 已经替换。
 * Reads the theme pushed to extensions: the id and title of the theme in use, and the variables' computed values with
 * `var()` substituted.
 */
export function readTheme(
  kind: "light" | "dark",
  applied: { id: string; title: string },
): { theme: Omit<ThemeInfo, "cssText">; background: string } {
  const style = getComputedStyle(document.documentElement);
  const tokens = Object.fromEntries(
    THEME_TOKEN_NAMES.map((name) => [name, style.getPropertyValue(name).trim()]).filter(
      ([, value]) => value,
    ),
  );
  const background =
    getComputedStyle(document.body).backgroundColor || (kind === "dark" ? "#0b0c0f" : "#ffffff");
  return { theme: { id: applied.id, title: applied.title, kind, tokens }, background };
}

/**
 * `--agw-workspace-backdrop` 的计算值是否为有效背景：不为空、不为 `none`，并且是合法的 `background-image` 值。
 * Whether the computed `--agw-workspace-backdrop` is an effective backdrop: not empty, not `none`, and a valid
 * `background-image` value.
 */
export function isWorkspaceBackdrop(value: string): boolean {
  const backdrop = value.trim();
  return (
    backdrop !== "" && backdrop !== "none" && window.CSS.supports("background-image", backdrop)
  );
}

/** 两次读取的结果是否相同。Whether two reads produced the same result. */
export function samePublishedTheme(left: PublishedTheme | null, right: PublishedTheme): boolean {
  return (
    left !== null &&
    left.background === right.background &&
    left.style?.styleId === right.style?.styleId &&
    left.theme.id === right.theme.id &&
    left.theme.title === right.theme.title &&
    left.theme.kind === right.theme.kind &&
    JSON.stringify(left.theme.tokens) === JSON.stringify(right.theme.tokens)
  );
}
