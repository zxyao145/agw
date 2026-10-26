// Monaco 的模块、worker 与语言（设计文档第 1 节）。只由 src/monaco 下的实现文件导入，入口通过 import() 按需加载。导入
// 路径按 monaco-editor 0.57.0 package.json 的 exports（"./*": "./esm/vs/*.js"）解析。
// Monaco's modules, workers and languages (design section 1). Only the implementation files under src/monaco import
// it; the entry loads them on demand through import(). Import paths resolve through the exports of monaco-editor
// 0.57.0's package.json ("./*": "./esm/vs/*.js").
import * as monaco from "monaco-editor/editor/editor.api";

// 编辑器功能模块：取自 monaco-editor 0.57.0 的 esm/vs/editor/editor.main.js，顺序相同，去掉其中的 TypeScript、CSS、HTML
// 语言服务（JSON 语言服务在后面导入）。editor.main.js 直接导入 codicon.css，exports 只映射 .js 文件，这里改为导入同一个
// CSS 的 features/codicon/register；codicon-modifiers.css 由 codeActionMenu、suggestWidget 等功能模块导入。
// Editor feature modules: taken from monaco-editor 0.57.0's esm/vs/editor/editor.main.js in the same order, without
// its TypeScript, CSS and HTML language services (the JSON language service is imported below). editor.main.js imports
// codicon.css directly, but the exports map only .js files, so features/codicon/register imports the same CSS here;
// codicon-modifiers.css is imported by feature modules such as codeActionMenu and suggestWidget.
import "monaco-editor/editor/contrib/anchorSelect/browser/anchorSelect";
import "monaco-editor/editor/contrib/bracketMatching/browser/bracketMatching";
import "monaco-editor/editor/contrib/caretOperations/browser/transpose";
import "monaco-editor/editor/contrib/clipboard/browser/clipboard";
import "monaco-editor/editor/contrib/codeAction/browser/codeActionContributions";
import "monaco-editor/editor/browser/widget/codeEditor/codeEditorWidget";
import "monaco-editor/editor/contrib/codelens/browser/codelensController";
import "monaco-editor/features/codicon/register";
import "monaco-editor/editor/contrib/colorPicker/browser/colorPickerContribution";
import "monaco-editor/editor/contrib/comment/browser/comment";
import "monaco-editor/editor/contrib/contextmenu/browser/contextmenu";
import "monaco-editor/editor/contrib/cursorUndo/browser/cursorUndo";
import "monaco-editor/editor/browser/widget/diffEditor/diffEditor.contribution";
import "monaco-editor/editor/contrib/diffEditorBreadcrumbs/browser/contribution";
import "monaco-editor/editor/contrib/dnd/browser/dnd";
import "monaco-editor/editor/contrib/documentSymbols/browser/documentSymbols";
import "monaco-editor/editor/contrib/dropOrPasteInto/browser/dropIntoEditorContribution";
import "monaco-editor/features/find/register";
import "monaco-editor/editor/contrib/floatingMenu/browser/floatingMenu.contribution";
import "monaco-editor/editor/contrib/folding/browser/folding";
import "monaco-editor/editor/contrib/fontZoom/browser/fontZoom";
import "monaco-editor/editor/contrib/format/browser/formatActions";
import "monaco-editor/editor/contrib/gotoError/browser/gotoError";
import "monaco-editor/editor/standalone/browser/quickAccess/standaloneGotoLineQuickAccess";
import "monaco-editor/editor/contrib/gotoSymbol/browser/link/goToDefinitionAtPosition";
import "monaco-editor/editor/contrib/gpu/browser/gpuActions";
import "monaco-editor/editor/contrib/hover/browser/hoverContribution";
import "monaco-editor/editor/contrib/indentation/browser/indentation";
import "monaco-editor/editor/contrib/inlayHints/browser/inlayHintsContribution";
import "monaco-editor/editor/contrib/inlineCompletions/browser/inlineCompletions.contribution";
import "monaco-editor/editor/contrib/inlineProgress/browser/inlineProgress";
import "monaco-editor/editor/contrib/inPlaceReplace/browser/inPlaceReplace";
import "monaco-editor/editor/contrib/insertFinalNewLine/browser/insertFinalNewLine";
import "monaco-editor/editor/standalone/browser/inspectTokens/inspectTokens";
import "monaco-editor/editor/standalone/browser/iPadShowKeyboard/iPadShowKeyboard";
import "monaco-editor/editor/contrib/lineSelection/browser/lineSelection";
import "monaco-editor/editor/contrib/linesOperations/browser/linesOperations";
import "monaco-editor/editor/contrib/linkedEditing/browser/linkedEditing";
import "monaco-editor/editor/contrib/links/browser/links";
import "monaco-editor/editor/contrib/longLinesHelper/browser/longLinesHelper";
import "monaco-editor/editor/contrib/middleScroll/browser/middleScroll.contribution";
import "monaco-editor/editor/contrib/multicursor/browser/multicursor";
import "monaco-editor/editor/contrib/parameterHints/browser/parameterHints";
import "monaco-editor/editor/contrib/placeholderText/browser/placeholderText.contribution";
import "monaco-editor/editor/standalone/browser/quickAccess/standaloneCommandsQuickAccess";
import "monaco-editor/editor/standalone/browser/quickAccess/standaloneHelpQuickAccess";
import "monaco-editor/editor/standalone/browser/quickAccess/standaloneGotoSymbolQuickAccess";
import "monaco-editor/editor/contrib/readOnlyMessage/browser/contribution";
import "monaco-editor/editor/standalone/browser/referenceSearch/standaloneReferenceSearch";
import "monaco-editor/editor/contrib/rename/browser/rename";
import "monaco-editor/editor/contrib/sectionHeaders/browser/sectionHeaders";
import "monaco-editor/editor/contrib/semanticTokens/browser/viewportSemanticTokens";
import "monaco-editor/editor/contrib/smartSelect/browser/smartSelect";
import "monaco-editor/editor/contrib/snippet/browser/snippetController2";
import "monaco-editor/editor/contrib/stickyScroll/browser/stickyScrollContribution";
import "monaco-editor/editor/contrib/suggest/browser/suggestInlineCompletions";
import "monaco-editor/editor/standalone/browser/toggleHighContrast/toggleHighContrast";
import "monaco-editor/editor/contrib/toggleTabFocusMode/browser/toggleTabFocusMode";
import "monaco-editor/editor/contrib/tokenization/browser/tokenization";
import "monaco-editor/editor/contrib/unicodeHighlighter/browser/unicodeHighlighter";
import "monaco-editor/editor/contrib/unusualLineTerminators/browser/unusualLineTerminators";
import "monaco-editor/editor/contrib/wordHighlighter/browser/wordHighlighter";
import "monaco-editor/editor/contrib/wordOperations/browser/wordOperations";
import "monaco-editor/editor/contrib/wordPartOperations/browser/wordPartOperations";
import "monaco-editor/editor/browser/coreCommands";
import "monaco-editor/editor/contrib/caretOperations/browser/caretOperations";
import "monaco-editor/editor/contrib/dropOrPasteInto/browser/copyPasteContribution";
import "monaco-editor/editor/contrib/find/browser/findController";
import "monaco-editor/editor/contrib/gotoSymbol/browser/goToCommands";
import "monaco-editor/editor/contrib/gotoError/browser/markerSelectionStatus";
import "monaco-editor/editor/contrib/semanticTokens/browser/documentSemanticTokens";
import "monaco-editor/editor/contrib/suggest/browser/suggestController";
import "monaco-editor/editor/common/standaloneStrings";

// 各语言的语法在第一次使用时加载；JSON 的高亮、校验与格式化来自 JSON 语言服务。Grammars load on first use; JSON
// highlighting, validation and formatting come from the JSON language service.
import "monaco-editor/basic-languages/monaco.contribution";
import { jsonDefaults } from "monaco-editor/languages/features/json/register";

const EDITOR_WORKER_LABEL = "editorWorkerService";
const JSON_WORKER_LABEL = "json";

self.MonacoEnvironment = {
  getWorker(_workerId, label) {
    // diff、链接等计算使用 editor worker，JSON 语言服务使用 json worker。Diff, links and similar computations use the
    // editor worker; the JSON language service uses the json worker.
    if (label === EDITOR_WORKER_LABEL) {
      return new Worker(new URL("./editor.worker.ts", import.meta.url), { type: "module" });
    }
    if (label === JSON_WORKER_LABEL) {
      return new Worker(new URL("./json.worker.ts", import.meta.url), { type: "module" });
    }
    throw new Error(`Monaco requested an unknown worker: ${label}`);
  },
};

jsonDefaults.setDiagnosticsOptions({
  validate: true,
  allowComments: true,
  trailingCommas: "ignore",
  schemaValidation: "ignore",
  enableSchemaRequest: false,
});

// Monaco 在 Linux 上把格式化绑定到 Ctrl+Shift+I，这里让 Shift+Alt+F 在全部平台上格式化。
// Monaco binds formatting to Ctrl+Shift+I on Linux; this makes Shift+Alt+F format on every platform.
monaco.editor.addKeybindingRule({
  keybinding: monaco.KeyMod.Shift | monaco.KeyMod.Alt | monaco.KeyCode.KeyF,
  command: "editor.action.formatDocument",
  when: "editorTextFocus && !editorReadonly",
});

// 字体加载完成后重新测量字符宽度。Remeasures character widths once fonts finish loading.
document.fonts.addEventListener("loadingdone", () => monaco.editor.remeasureFonts());

export { monaco };
