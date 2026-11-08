import type { Disposable, ExtensionApi, ThemeInfo } from "@agw/extension-sdk";

import { resolveTextEditorFontSizes, type TextEditorSettings } from "./text-editor-settings";

/**
 * 在 `document.documentElement` 上写入 `--agw-font-size-document`（Mixed 与 Preview 的文档字号）：立即写入一次，之后每次
 * 主题变化时用内存中的设置与新的基础字号重新计算。
 * Writes `--agw-font-size-document` (the document font size of Mixed and Preview) on `document.documentElement`: once
 * now, and again on every theme change from the in-memory settings and the new base size.
 */
export function applyDocumentFontSize(api: ExtensionApi, settings: TextEditorSettings): Disposable {
  const write = (theme: ThemeInfo) => {
    const { document: size } = resolveTextEditorFontSizes(settings, theme.tokens);
    document.documentElement.style.setProperty("--agw-font-size-document", `${size}px`);
  };
  write(api.theme.get());
  return api.theme.onDidChange(write);
}
