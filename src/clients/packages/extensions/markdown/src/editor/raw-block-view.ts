import type { Node as ProseMirrorNode } from "@tiptap/pm/model";
import type { NodeView, ViewMutationRecord } from "@tiptap/pm/view";

// 原文块的 node view（设计 5.7）：`rawBlock` 与 `frontMatter` 显示为等宽字体的块，左上角的标签说明种类，文字可以直接编辑。
// 不使用 React。
// The raw block node view (design section 5.7): `rawBlock` and `frontMatter` show as monospace blocks whose top-left
// label names the kind; the text is edited in place. No React.

const RAW_BLOCK_LABELS: Record<string, string> = {
  html: "HTML",
  definition: "Link definition",
  footnote: "Footnote",
};

function labelOf(node: ProseMirrorNode): string {
  if (node.type.name === "frontMatter") return "Front matter";
  const label = RAW_BLOCK_LABELS[String(node.attrs.kind)];
  if (label === undefined) throw new Error(`Unknown raw block kind ${String(node.attrs.kind)}.`);
  return label;
}

export class RawBlockView implements NodeView {
  public readonly dom: HTMLElement;
  public readonly contentDOM: HTMLElement;
  private readonly label: HTMLElement;
  private node: ProseMirrorNode;

  public constructor(node: ProseMirrorNode) {
    this.node = node;
    this.dom = document.createElement("div");
    this.dom.className = "raw-block";
    this.label = document.createElement("span");
    this.label.className = "raw-block-label";
    this.label.contentEditable = "false";
    const pre = document.createElement("pre");
    this.contentDOM = document.createElement("code");
    pre.append(this.contentDOM);
    this.dom.append(this.label, pre);
    this.render();
  }

  public update(node: ProseMirrorNode): boolean {
    if (node.type !== this.node.type) return false;
    this.node = node;
    this.render();
    return true;
  }

  public ignoreMutation(mutation: ViewMutationRecord): boolean {
    return !this.contentDOM.contains(mutation.target);
  }

  private render(): void {
    this.label.textContent = labelOf(this.node);
    this.dom.dataset.kind =
      this.node.type.name === "frontMatter" ? "front-matter" : String(this.node.attrs.kind);
  }
}
