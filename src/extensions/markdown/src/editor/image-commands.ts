import type { Editor } from "@tiptap/core";
import type { Node as ProseMirrorNode } from "@tiptap/pm/model";
import { NodeSelection, TextSelection, type Transaction } from "@tiptap/pm/state";

import { markdownDefinitionsKey, resolveReference } from "./markdown-definitions";

// 图片的命令（设计 5.7）：`/` 菜单的 Image 在地址输入框确认后插入图片；选中图片时的浮层修改地址与 alt。
// Image commands (design section 5.7): the `/` menu's Image inserts an image once its address is confirmed; the
// popover of a selected image changes its address and alt text.

/** 图片显示的地址：引用式图片由当前定义解析。The address an image shows; reference images resolve it from the
 *  current definitions. */
export function imageAddress(editor: Editor, image: ProseMirrorNode): string {
  if (image.attrs.reference === null) return String(image.attrs.src ?? "");
  const definitions = markdownDefinitionsKey.getState(editor.state)!;
  return resolveReference(definitions, String(image.attrs.reference))?.href ?? "";
}

function canInsertImage(doc: ProseMirrorNode, position: number): boolean {
  const $pos = doc.resolve(position);
  return $pos.parent.canReplaceWith($pos.index(), $pos.index(), doc.type.schema.nodes.image);
}

/**
 * `/` 菜单的 Image 记录的插入位置经过之后的事务后的位置（`transactions` 为一次派发的事务与它追加的事务）。位置两边的
 * 内容都被删除（例如所在的 paragraph 被删除，或文档重新载入），或新位置不能放图片时返回 null，地址输入框随之关闭。
 * The insert position the `/` menu's Image recorded, after later transactions (`transactions` holds one dispatched
 * transaction and those appended to it). Returns null, which closes the address input, when the content on both sides
 * of the position was deleted (for example its paragraph was deleted or the document reloaded) or the new position
 * cannot hold an image.
 */
export function mapImageInsertPosition(
  position: number,
  transactions: readonly Transaction[],
): number | null {
  let mapped = position;
  for (const tr of transactions) {
    const result = tr.mapping.mapResult(mapped);
    if (result.deletedAcross) return null;
    mapped = result.pos;
  }
  return canInsertImage(transactions.at(-1)!.doc, mapped) ? mapped : null;
}

/** 在 `position` 插入地址为 `src` 的图片，光标放在图片之后。Inserts an image with `src` at `position` and puts the
 *  cursor after it. */
export function insertImage(editor: Editor, position: number, src: string): void {
  const { state } = editor;
  if (!canInsertImage(state.doc, position)) throw new Error(`An image cannot go at ${position}.`);
  const image = state.schema.nodes.image.create({ src, alt: "" });
  const tr = state.tr.insert(position, image);
  tr.setSelection(TextSelection.near(tr.doc.resolve(position + image.nodeSize)));
  editor.view.dispatch(tr);
}

/**
 * 修改 `position` 处的图片。地址改变时引用式图片改为普通图片（与链接相同）；只改 alt 时保留引用。
 * Changes the image at `position`. A changed address turns a reference image into a plain one (as for links); a
 * change of the alt text alone keeps the reference.
 */
export function updateImage(
  editor: Editor,
  position: number,
  values: { src: string; alt: string },
): void {
  const { state } = editor;
  const image = state.doc.nodeAt(position);
  if (image?.type.name !== "image") throw new Error(`No image at ${position}.`);
  const keepReference =
    image.attrs.reference !== null && values.src === imageAddress(editor, image);
  const attrs = keepReference
    ? { ...image.attrs, alt: values.alt }
    : { ...image.attrs, src: values.src, alt: values.alt, reference: null, referenceKind: null };
  const tr = state.tr.setNodeMarkup(position, undefined, attrs);
  tr.setSelection(NodeSelection.create(tr.doc, position));
  editor.view.dispatch(tr);
}
