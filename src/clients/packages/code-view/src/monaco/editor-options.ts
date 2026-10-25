import type { HostFont } from "./host-appearance";
import { detectLanguage } from "./languages";
import { monaco } from "./setup";

let nextModel = 0;

/**
 * Source 的编辑器选项（设计文档第 1 节）；只读时另加 `readOnly` 与 `domReadOnly`。
 * Source's editor options (design section 1); read-only adds `readOnly` and `domReadOnly`.
 */
export function editorOptions(
  font: HostFont,
  options: { readOnly: boolean; wordWrap: boolean },
): monaco.editor.IEditorOptions {
  return {
    readOnly: options.readOnly,
    domReadOnly: options.readOnly,
    automaticLayout: true,
    minimap: { enabled: false },
    scrollBeyondLastLine: false,
    wordWrap: options.wordWrap ? "on" : "off",
    // 悬停与菜单等不被容器的 overflow 裁剪。Hovers and menus are not clipped by the container's overflow.
    fixedOverflowWidgets: true,
    ...font,
  };
}

/** DiffEditor 的设置，两侧都只读。DiffEditor settings; both sides are read-only. */
export const DIFF_OPTIONS: monaco.editor.IDiffEditorOptions = {
  originalEditable: false,
  renderSideBySide: true,
  // 原始侧的行评论需要左侧编辑器，窄宽度下也保持两栏。Original-side comments need the left editor, so narrow widths
  // keep two columns.
  useInlineViewWhenSpaceIsLimited: false,
  enableSplitViewResizing: true,
  ignoreTrimWhitespace: false,
  hideUnchangedRegions: {
    enabled: true,
    contextLineCount: 3,
    minimumLineCount: 3,
    revealLineCount: 20,
  },
  renderGutterMenu: false,
  renderMarginRevertIcon: false,
};

/**
 * 一个文件的 model：语言由 `detectLanguage` 按文件名决定；URI 的 authority 使每个 model 的 URI 不同。
 * A file's model: `detectLanguage` decides the language from the file name, and the URI authority keeps every model URI
 * distinct.
 */
export function createCodeModel(text: string, filePath: string): monaco.editor.ITextModel {
  const normalized = filePath.replaceAll("\\", "/").replace(/^\/+/u, "");
  const fileName = normalized.split("/").pop() ?? normalized;
  const uri = monaco.Uri.from({
    scheme: "agw-code-view",
    authority: `m${nextModel++}`,
    path: `/${normalized}`,
  });
  return monaco.editor.createModel(
    text,
    detectLanguage(fileName, monaco.languages.getLanguages()),
    uri,
  );
}
