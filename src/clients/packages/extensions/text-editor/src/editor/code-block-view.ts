import type { ExtensionApi } from "@agw/extension-sdk";
import type { Editor } from "@tiptap/core";
import type { Node as ProseMirrorNode } from "@tiptap/pm/model";
import type { NodeView, ViewMutationRecord } from "@tiptap/pm/view";

import { errorMessage } from "../shared/errors";
import { languageChoice, setCodeBlockLanguage, type CodeLanguageEntry } from "./code-languages";

// 代码块的 node view（设计 5.7）：标题栏有语言选择与 Copy 按钮，内容由 CodeBlockLowlight 的装饰高亮。不使用 React。
// The code block node view (design section 5.7): a header with the language choice and a Copy button; the content is
// highlighted by CodeBlockLowlight's decorations. No React.

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

  public constructor(
    node: ProseMirrorNode,
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
    this.header.append(this.select, copy);
    const pre = document.createElement("pre");
    this.contentDOM = document.createElement("code");
    pre.append(this.contentDOM);
    this.dom.append(this.header, pre);
    this.render();
  }

  public update(node: ProseMirrorNode): boolean {
    if (node.type !== this.node.type) return false;
    const languageChanged = node.attrs.language !== this.node.attrs.language;
    this.node = node;
    if (languageChanged) this.render();
    return true;
  }

  public stopEvent(event: Event): boolean {
    return this.header.contains(event.target as Node);
  }

  public ignoreMutation(mutation: ViewMutationRecord): boolean {
    return !this.contentDOM.contains(mutation.target);
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
