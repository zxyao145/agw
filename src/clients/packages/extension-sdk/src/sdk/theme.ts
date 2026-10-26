import type { ThemeChangedNotification, ThemeInfo } from "../api";
import { createExtensionError } from "../rpc";

const TYPOGRAPHY_STYLESHEET = "/__agw/typography.css";
/** 宿主推送的字号，CSS 主题下由 SDK 作为 inline 属性管理。The pushed font size, managed by the SDK as an inline property under CSS themes. */
const FONT_SIZE_PROPERTY = "--agw-font-size-base";

/** SDK 保存的主题状态：完整的 `ThemeInfo` 与 CSS 文本的标识。The theme state the SDK keeps: the complete `ThemeInfo` and the CSS text id. */
export type ThemeState = { theme: ThemeInfo; styleId: string | null };

/**
 * 把 `theme.changed` 合并为完整状态：`unchanged` 的标识必须与已有状态一致；字段省略不表示保留或清除样式。
 * Merges `theme.changed` into the complete state: an `unchanged` id must match the current state; omitted fields do
 * not mean keeping or clearing the style.
 */
export function mergeThemeChange(
  current: ThemeState,
  change: ThemeChangedNotification,
): ThemeState {
  const { style } = change;
  if (style.operation === "unchanged") {
    if (style.styleId !== current.styleId) {
      throw createExtensionError(
        "ERR_PROTOCOL",
        `theme.changed kept style ${String(style.styleId)}, but this page holds ${String(current.styleId)}.`,
      );
    }
    return {
      theme:
        current.theme.cssText === undefined
          ? { ...change.theme }
          : { ...change.theme, cssText: current.theme.cssText },
      styleId: current.styleId,
    };
  }
  if (style.operation === "replace") {
    return { theme: { ...change.theme, cssText: style.cssText }, styleId: style.styleId };
  }
  return { theme: { ...change.theme }, styleId: null };
}

/** 一个 document 中主题系统拥有的部分。What the theme system owns in one document. */
type DocumentThemeState = {
  defaults: CSSStyleSheet | null;
  theme: CSSStyleSheet | null;
  styleId: string | null;
  /** 写到根元素 inline style 的变量。Variables written to the root element's inline style. */
  inline: Set<string>;
};

const documents = new WeakMap<Document, DocumentThemeState>();

function documentState(target: Document): DocumentThemeState {
  let state = documents.get(target);
  if (!state) {
    state = { defaults: null, theme: null, styleId: null, inline: new Set() };
    documents.set(target, state);
    target.defaultView?.addEventListener("pagehide", () => releaseDocumentTheme(target), {
      once: true,
    });
  }
  return state;
}

function owned(state: DocumentThemeState): CSSStyleSheet[] {
  return [state.defaults, state.theme].filter((sheet): sheet is CSSStyleSheet => sheet !== null);
}

function setInline(
  root: HTMLElement,
  state: DocumentThemeState,
  tokens: Record<string, string>,
): void {
  for (const name of state.inline) {
    if (!(name in tokens)) root.style.removeProperty(name);
  }
  for (const [name, value] of Object.entries(tokens)) root.style.setProperty(name, value);
  state.inline = new Set(Object.keys(tokens));
}

/**
 * 在 document 中应用主题：JSON 主题与 Default 写 inline 变量并移除主题样式表；CSS 主题清除之前写入的 inline 变量，只保留字号，
 * 使用这个 document 自己的默认变量样式表与主题样式表，`styleId` 不变时复用文本。其他代码的 adopted stylesheet 保持不变。
 * Applies the theme to a document: JSON themes and Default write inline variables and remove the theme stylesheets;
 * CSS themes clear the inline variables written before except the font size and use this document's own default
 * variable and theme stylesheets, reusing the text while `styleId` is unchanged. Adopted stylesheets of other code stay.
 */
export function applyTheme(
  state: ThemeState,
  target: Document,
  options: { extensionId: string; defaultsText: string },
): void {
  if (!target.head.querySelector("link[data-agw-typography]")) {
    const link = target.createElement("link");
    link.rel = "stylesheet";
    link.href = TYPOGRAPHY_STYLESHEET;
    link.dataset.agwTypography = "";
    target.head.append(link);
  }
  const root = target.documentElement;
  root.dataset.agwDocument = "extension";
  root.dataset.agwExtension = options.extensionId;
  const owner = documentState(target);
  const { theme } = state;
  if (theme.cssText !== undefined && state.styleId !== null) {
    const fontSize = theme.tokens[FONT_SIZE_PROPERTY];
    setInline(root, owner, fontSize === undefined ? {} : { [FONT_SIZE_PROPERTY]: fontSize });
    const Sheet = target.defaultView!.CSSStyleSheet;
    if (!owner.defaults) {
      owner.defaults = new Sheet();
      owner.defaults.replaceSync(options.defaultsText);
    }
    owner.theme ??= new Sheet();
    if (owner.styleId !== state.styleId) {
      owner.theme.replaceSync(theme.cssText);
      owner.styleId = state.styleId;
    }
    const adopted = target.adoptedStyleSheets;
    const missing = owned(owner).filter((sheet) => !adopted.includes(sheet));
    if (missing.length > 0) target.adoptedStyleSheets = [...adopted, ...missing];
  } else {
    if (owner.defaults || owner.theme) {
      const sheets = owned(owner);
      target.adoptedStyleSheets = target.adoptedStyleSheets.filter(
        (sheet) => !sheets.includes(sheet),
      );
      owner.defaults = null;
      owner.theme = null;
      owner.styleId = null;
    }
    setInline(root, owner, theme.tokens);
  }
  root.classList.toggle("dark", theme.kind === "dark");
  root.style.colorScheme = theme.kind;
}

/** 页面卸载时移除主题样式表并释放文本。Removes the theme stylesheets and releases their text when the page unloads. */
export function releaseDocumentTheme(target: Document): void {
  const owner = documents.get(target);
  if (!owner) return;
  const sheets = owned(owner);
  if (sheets.length > 0) {
    target.adoptedStyleSheets = target.adoptedStyleSheets.filter(
      (sheet) => !sheets.includes(sheet),
    );
  }
  documents.delete(target);
}
