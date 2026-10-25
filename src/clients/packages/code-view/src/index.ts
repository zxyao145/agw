// Source 与 diff 的 Monaco 编辑器。入口不引用 Monaco，Monaco 在第一次显示时按需加载；创建可编辑文本模型的
// `loadCodeTextModel` 在 `@agw/code-view/text-model` 中，只有 Desktop 使用。
// Monaco editors for Source and diffs. The entry does not import Monaco, which loads on first display;
// `loadCodeTextModel`, which creates editable text models, lives in `@agw/code-view/text-model` for Desktop alone.
export { CodeViewAppearanceProvider } from "./appearance";
export { CodeDiffViewer } from "./code-diff-viewer";
export { CodeViewer } from "./code-viewer";
export { type LineChange, remapLine } from "./line-remap";
export {
  POPUP_ITEM_CLASS,
  POPUP_NOTE_CLASS,
  PopupButton,
  STATUS_BUTTON_CLASS,
} from "./popup-button";
export type { CodeEol, CodeTextDisposable, CodeTextFile, CodeTextModel } from "./text-model";
export type {
  CodeDiffSide,
  CodeDiffViewerProps,
  CodeViewerProps,
  CodeViewSideProps,
  EditableCodeContent,
  LineZone,
  LineZoneAction,
  TextCodeContent,
} from "./types";
