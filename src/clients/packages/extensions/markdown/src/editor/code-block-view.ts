import type { ExtensionApi } from "@agw/extension-sdk";
import type { Editor } from "@tiptap/core";
import type { Node as ProseMirrorNode } from "@tiptap/pm/model";
import type { Decoration, NodeView, ViewMutationRecord } from "@tiptap/pm/view";

import { errorMessage, notifyFailure } from "../shared/errors";
import { MermaidDiagram } from "../shared/mermaid-diagram";
import { isMermaidLanguage } from "../shared/mermaid-render";
import { languageChoice, setCodeBlockLanguage, type CodeLanguageEntry } from "./code-languages";
import { isDiagramOnly, isMermaidEditing, setMermaidDiagramOnly } from "./mermaid-editing";

// 代码块的 node view（设计 5.7）：标题栏有语言选择与 Copy 按钮，内容由 CodeBlockLowlight 的装饰高亮。Mermaid 代码块在
// 源码下方显示图表，标题栏多一个 "Diagram only" 开关（Mermaid 设计第 2、3 节）：默认视图中图表渲染出来并且不在编辑状态时
// 隐藏源码；"Diagram only" 视图始终隐藏源码。不使用 React。
// The code block node view (design section 5.7): a header with the language choice and a Copy button; the content is
// highlighted by CodeBlockLowlight's decorations. Mermaid code blocks show the diagram below the source, and the header
// gets a "Diagram only" toggle (Mermaid design sections 2 and 3): the default view hides the source once the diagram
// renders and the block is not being edited; the Diagram only view always hides the source. No React.

/**
 * 用 `copy` 事件把文字放进剪贴板。extension 没有剪贴板权限，`document.execCommand("copy")` 在用户点击时可用。
 * Puts text on the clipboard through a `copy` event. The extension has no clipboard permission, and
 * `document.execCommand("copy")` works during a user click.
 */
function copyText(text: string): void {
  const onCopy = (event: ClipboardEvent) => {
    event.clipboardData!.setData("text/plain", text);
    event.preventDefault();
  };
  document.addEventListener("copy", onCopy);
  try {
    if (!document.execCommand("copy")) throw new Error("The browser refused to copy.");
  } finally {
    document.removeEventListener("copy", onCopy);
  }
}

function option(value: string, label: string): HTMLOptionElement {
  const element = document.createElement("option");
  element.value = value;
  element.textContent = label;
  return element;
}

export class CodeBlockView implements NodeView {
  public readonly dom: HTMLElement;
  public readonly contentDOM: HTMLElement;
  private readonly header: HTMLElement;
  private readonly select: HTMLSelectElement;
  private node: ProseMirrorNode;
  // 选择框的全部语言在第一次使用时才创建：文档中有大量代码块时不创建大量选项。
  // The select gets all languages on first use, so documents with many code blocks do not create many options.
  private populated = false;
  /** Mermaid 代码块的图表；其他语言为 null。The diagram of a Mermaid code block; null for other languages. */
  private diagram: MermaidDiagram | null = null;
  /** 切换 "Diagram only" 视图的按钮，只在 Mermaid 代码块中显示。The Diagram only view toggle, shown only in Mermaid
   *  code blocks. */
  private readonly viewToggle: HTMLButtonElement;
  private diagramOnly = false;

  public constructor(
    node: ProseMirrorNode,
    decorations: readonly Decoration[],
    private readonly editor: Editor,
    private readonly getPos: () => number | undefined,
    private readonly api: ExtensionApi,
    private readonly languages: readonly CodeLanguageEntry[],
  ) {
    this.node = node;
    this.dom = document.createElement("div");
    this.dom.className = "code-block";
    this.header = document.createElement("div");
    this.header.className = "code-block-header";
    this.header.contentEditable = "false";
    this.select = document.createElement("select");
    this.select.className = "code-block-language";
    this.select.setAttribute("aria-label", "Code block language");
    const populate = () => this.populate();
    this.select.addEventListener("pointerdown", populate);
    this.select.addEventListener("focus", populate);
    this.select.addEventListener("change", () => {
      const position = this.getPos();
      if (position === undefined) throw new Error("The code block is no longer in the document.");
      setCodeBlockLanguage(this.editor, position, this.select.value);
    });
    const copy = document.createElement("button");
    copy.type = "button";
    copy.className = "code-block-copy";
    copy.textContent = "Copy";
    copy.addEventListener("click", () => {
      try {
        copyText(this.node.textContent);
      } catch (error) {
        this.api.ui.notify({
          type: "error",
          message: `Could not copy the code: ${errorMessage(error)}`,
        });
      }
    });
    this.viewToggle = document.createElement("button");
    this.viewToggle.type = "button";
    this.viewToggle.className = "code-block-view";
    this.viewToggle.textContent = "Diagram only";
    this.viewToggle.hidden = true;
    this.viewToggle.addEventListener("click", () => {
      const position = this.getPos();
      if (position === undefined) throw new Error("The code block is no longer in the document.");
      setMermaidDiagramOnly(this.editor.view, position, !this.diagramOnly);
    });
    const actions = document.createElement("span");
    actions.className = "code-block-actions";
    actions.append(this.viewToggle, copy);
    this.header.append(this.select, actions);
    const pre = document.createElement("pre");
    this.contentDOM = document.createElement("code");
    pre.append(this.contentDOM);
    this.dom.append(this.header, pre);
    this.render();
    this.showDiagram(decorations);
  }

  public update(node: ProseMirrorNode, decorations: readonly Decoration[]): boolean {
    if (node.type !== this.node.type) return false;
    const languageChanged = node.attrs.language !== this.node.attrs.language;
    this.node = node;
    if (languageChanged) this.render();
    this.showDiagram(decorations);
    return true;
  }

  public stopEvent(event: Event): boolean {
    const target = event.target as Node;
    return this.header.contains(target) || Boolean(this.diagram?.dom.contains(target));
  }

  public ignoreMutation(mutation: ViewMutationRecord): boolean {
    return !this.contentDOM.contains(mutation.target);
  }

  public destroy(): void {
    this.diagram?.destroy();
  }

  /** Mermaid 代码块按源码更新图表，并标记视图与编辑状态；其他语言移除图表。Mermaid code blocks update the diagram
   *  from the source and mark the view and the editing state; other languages remove the diagram. */
  private showDiagram(decorations: readonly Decoration[]): void {
    if (!isMermaidLanguage(this.node.attrs.language as string | null)) {
      if (this.diagram === null) return;
      this.diagram.destroy();
      this.diagram.dom.remove();
      this.diagram = null;
      this.diagramOnly = false;
      this.viewToggle.hidden = true;
      delete this.dom.dataset.diagram;
      delete this.dom.dataset.view;
      this.dom.removeAttribute("data-editing");
      return;
    }
    if (this.diagram === null) {
      const diagram = new MermaidDiagram(this.api, () => {
        this.dom.dataset.diagram = diagram.state;
      });
      diagram.dom.addEventListener("mousedown", (event) => this.pressDiagram(event));
      diagram.dom.addEventListener("click", () => this.clickDiagram());
      this.dom.append(diagram.dom);
      this.diagram = diagram;
    }
    this.diagram.update(this.node.textContent);
    this.dom.dataset.diagram = this.diagram.state;
    this.diagramOnly = isDiagramOnly(decorations);
    this.viewToggle.hidden = false;
    this.viewToggle.setAttribute("aria-pressed", String(this.diagramOnly));
    if (this.diagramOnly) this.dom.dataset.view = "diagram";
    else delete this.dom.dataset.view;
    this.dom.toggleAttribute("data-editing", isMermaidEditing(decorations));
  }

  /**
   * 在图表上按下：默认视图中光标放到源码末尾；"Diagram only" 视图中不改变选区，点击时由宿主预览图表。
   * Pressing on the diagram: the default view puts the cursor at the end of the source; the Diagram only view keeps the
   * selection, and the click has the host preview the diagram.
   */
  private pressDiagram(event: MouseEvent): void {
    if (event.button !== 0) return;
    event.preventDefault();
    if (this.diagramOnly) return;
    const position = this.getPos();
    if (position === undefined) throw new Error("The code block is no longer in the document.");
    this.editor.commands.focus(position + this.node.nodeSize - 1);
  }

  private clickDiagram(): void {
    if (this.diagramOnly && this.diagram!.state === "rendered") {
      notifyFailure(this.api, this.diagram!.openPreview());
    }
  }

  private populate(): void {
    if (this.populated) return;
    this.populated = true;
    this.render();
  }

  /** 选择框只含当前语言，或在使用后含全部语言。The select holds the current language only, or all once used. */
  private render(): void {
    const choice = languageChoice(this.node.attrs.language as string | null, this.languages);
    const options = [option("", "Plain text")];
    if (choice.unknown !== null) options.push(option(choice.unknown, choice.unknown));
    for (const language of choice.languages) {
      if (this.populated || language.name === choice.value) {
        options.push(option(language.name, language.label));
      }
    }
    this.select.replaceChildren(...options);
    this.select.value = choice.value;
  }
}
