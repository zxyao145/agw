// 作用于一行的操作（设计文档第 6 节）：鼠标所在行的行号列左侧显示的按钮，以及右键菜单中作用于光标所在行的同名操作；另有
// 整行标记。按钮放在 overlay widget 中，编辑器不需要 glyph margin。
// The action on a line (design section 6): a button at the left of the line-number column on the line under the mouse,
// the same action in the context menu for the cursor line, and whole-line marks. The button lives in an overlay widget,
// so the editor needs no glyph margin.
import type { LineZoneAction } from "../types";
import { monaco } from "./setup";

const ACTION_ID = "agw.codeView.lineAction";
const CAN_RUN_CONTEXT_KEY = "agwCodeViewCanRunLineAction";
const MARKED_LINE_CLASS = "agw-code-view-marked-line";
const HIDDEN_CLASS = "is-hidden";
/** 按钮的边长（px），与 code-view.css 相同。The button size in px, as in code-view.css. */
const BUTTON_SIZE = 16;

/** 鼠标在这些位置时，按钮显示在所在行。Mouse targets whose line shows the button. */
const LINE_TARGETS = new Set([
  monaco.editor.MouseTargetType.GUTTER_GLYPH_MARGIN,
  monaco.editor.MouseTargetType.GUTTER_LINE_NUMBERS,
  monaco.editor.MouseTargetType.GUTTER_LINE_DECORATIONS,
  monaco.editor.MouseTargetType.CONTENT_TEXT,
  monaco.editor.MouseTargetType.CONTENT_EMPTY,
]);

let nextWidgetId = 0;

export class LineActionController {
  private action: LineZoneAction | null = null;
  private hoveredLine: number | null = null;
  private registration: monaco.IDisposable | null = null;
  private registeredLabel: string | null = null;
  private readonly widgetId = `agw.codeView.lineAction.${nextWidgetId++}`;
  private readonly anchor: HTMLDivElement;
  private readonly button: HTMLButtonElement;
  private readonly widget: monaco.editor.IOverlayWidget;
  private readonly canRun: monaco.editor.IContextKey<boolean>;
  private readonly marks: monaco.editor.IEditorDecorationsCollection;
  private readonly listeners: monaco.IDisposable[];

  public constructor(private readonly editor: monaco.editor.IStandaloneCodeEditor) {
    // overlay widget 的位置为 null，锚点停在编辑器左上角，按钮由我们定位。The overlay widget has a null position, so
    // the anchor stays at the editor's top left and we position the button.
    this.anchor = document.createElement("div");
    this.anchor.className = "agw-code-view-zone-anchor";
    this.button = document.createElement("button");
    this.button.type = "button";
    this.button.tabIndex = -1;
    this.button.className = `agw-code-view-line-action codicon codicon-add rounded bg-blue-500 text-white hover:bg-blue-600 ${HIDDEN_CLASS}`;
    // 按下时执行，不移走编辑器的焦点与选区。Runs on press without taking the editor's focus or selection.
    this.button.addEventListener("mousedown", (event) => {
      event.preventDefault();
      event.stopPropagation();
      if (event.button === 0 && this.hoveredLine !== null) this.runOn(this.hoveredLine);
    });
    this.anchor.append(this.button);
    this.widget = {
      getId: () => this.widgetId,
      getDomNode: () => this.anchor,
      getPosition: () => null,
    };
    editor.addOverlayWidget(this.widget);
    this.canRun = editor.createContextKey(CAN_RUN_CONTEXT_KEY, false);
    this.marks = editor.createDecorationsCollection();
    this.listeners = [
      editor.onMouseMove((event) => {
        // 鼠标移到按钮上时保持显示。The button stays while the mouse is on it.
        if (
          event.target.type === monaco.editor.MouseTargetType.OVERLAY_WIDGET &&
          event.target.detail === this.widgetId
        ) {
          return;
        }
        const line = LINE_TARGETS.has(event.target.type)
          ? (event.target.position?.lineNumber ?? null)
          : null;
        this.showAt(line);
      }),
      editor.onMouseLeave(() => this.showAt(null)),
      editor.onDidScrollChange(() => this.place()),
      editor.onDidLayoutChange(() => this.place()),
      editor.onDidChangeCursorPosition(() => this.updateCanRun()),
    ];
  }

  /** 设置操作；null 表示没有操作。Sets the action; null means none. */
  public setAction(action: LineZoneAction | null): void {
    this.action = action;
    if (action?.label !== this.registeredLabel) {
      this.registration?.dispose();
      this.registration = null;
      this.registeredLabel = null;
      if (action !== null) {
        this.registration = this.editor.addAction({
          id: ACTION_ID,
          label: action.label,
          precondition: CAN_RUN_CONTEXT_KEY,
          contextMenuGroupId: "navigation",
          contextMenuOrder: 0,
          run: (editor) => {
            const line = editor.getPosition()?.lineNumber;
            if (line !== undefined) this.runOn(line);
          },
        });
        this.registeredLabel = action.label;
      }
    }
    this.updateCanRun();
    this.showAt(this.hoveredLine);
  }

  /** 整行标出的行。Lines marked across the whole line. */
  public setMarkedLines(lines: readonly number[]): void {
    this.marks.set(
      lines.map((line) => ({
        range: new monaco.Range(line, 1, line, 1),
        options: {
          isWholeLine: true,
          className: MARKED_LINE_CLASS,
          marginClassName: MARKED_LINE_CLASS,
        },
      })),
    );
  }

  public dispose(): void {
    for (const listener of this.listeners) listener.dispose();
    this.registration?.dispose();
    this.marks.clear();
    this.editor.removeOverlayWidget(this.widget);
  }

  private runOn(line: number): void {
    if (this.action?.canRun(line)) this.action.run(line);
  }

  private updateCanRun(): void {
    const line = this.editor.getPosition()?.lineNumber;
    this.canRun.set(line !== undefined && (this.action?.canRun(line) ?? false));
  }

  private showAt(line: number | null): void {
    const action = this.action;
    if (line === null || action === null || !action.canRun(line)) {
      this.hoveredLine = null;
      this.button.classList.add(HIDDEN_CLASS);
      return;
    }
    this.hoveredLine = line;
    this.button.classList.remove(HIDDEN_CLASS);
    this.button.title = action.label;
    this.button.setAttribute("aria-label", `${action.label}, line ${line}`);
    this.place();
  }

  /** 按钮在所在行中垂直居中。Centers the button vertically in its line. */
  private place(): void {
    const line = this.hoveredLine;
    if (line === null) return;
    const lineHeight = this.editor.getOption(monaco.editor.EditorOption.lineHeight);
    const top = this.editor.getTopForLineNumber(line) - this.editor.getScrollTop();
    this.button.style.top = `${top + (lineHeight - BUTTON_SIZE) / 2}px`;
  }
}
