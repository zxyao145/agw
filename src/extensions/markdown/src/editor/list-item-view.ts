import type { Node as ProseMirrorNode } from "@tiptap/pm/model";
import type { EditorView, NodeView, ViewMutationRecord } from "@tiptap/pm/view";

// 列表项的 node view（设计 5.7）：`checked` 不为 null 的任务项左侧显示复选框，点击切换 `checked`。不使用 React。
// The list item node view (design section 5.7): a task item (`checked` not null) shows a checkbox on its left, and
// clicking it toggles `checked`. No React.

export class ListItemView implements NodeView {
  public readonly dom: HTMLLIElement;
  public readonly contentDOM: HTMLElement;
  private readonly checkbox: HTMLInputElement | null = null;
  private node: ProseMirrorNode;

  public constructor(node: ProseMirrorNode, view: EditorView, getPos: () => number | undefined) {
    this.node = node;
    this.dom = document.createElement("li");
    if (node.attrs.checked === null) {
      this.contentDOM = this.dom;
      return;
    }
    this.dom.className = "task-item";
    const label = document.createElement("label");
    label.className = "task-checkbox";
    label.contentEditable = "false";
    this.checkbox = document.createElement("input");
    this.checkbox.type = "checkbox";
    // 点击复选框不移动编辑器的选区。Clicking the checkbox does not move the editor selection.
    this.checkbox.addEventListener("mousedown", (event) => event.preventDefault());
    // click 时 checked 已经切换。`checked` has already toggled when click fires.
    this.checkbox.addEventListener("click", () => {
      const position = getPos();
      if (position === undefined) throw new Error("The task item is no longer in the document.");
      view.dispatch(
        view.state.tr.setNodeMarkup(position, undefined, {
          ...this.node.attrs,
          checked: this.checkbox!.checked,
        }),
      );
    });
    label.append(this.checkbox);
    this.contentDOM = document.createElement("div");
    this.contentDOM.className = "task-content";
    this.dom.append(label, this.contentDOM);
    this.render();
  }

  public update(node: ProseMirrorNode): boolean {
    // 普通项与任务项之间切换时重新创建。Switching between a plain item and a task item creates a new view.
    if (
      node.type !== this.node.type ||
      (node.attrs.checked === null) !== (this.checkbox === null)
    ) {
      return false;
    }
    this.node = node;
    if (this.checkbox !== null) this.render();
    return true;
  }

  public stopEvent(event: Event): boolean {
    return this.checkbox !== null && event.target === this.checkbox;
  }

  public ignoreMutation(mutation: ViewMutationRecord): boolean {
    return this.checkbox !== null && !this.contentDOM.contains(mutation.target);
  }

  private render(): void {
    const checked = this.node.attrs.checked === true;
    this.checkbox!.checked = checked;
    this.dom.dataset.checked = String(checked);
  }
}
