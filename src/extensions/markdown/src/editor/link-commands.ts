import type { ExtensionApi } from "@agw/extension-sdk";
import { getMarkRange, type Editor } from "@tiptap/core";
import type { EditorState } from "@tiptap/pm/state";

import { classifyLink } from "../markdown/markdown-links";
import { notifyFailure } from "../shared/errors";
import { markdownDefinitionsKey, resolveReference } from "./markdown-definitions";

// 链接的命令（设计 5.7）：Apply 设置地址（引用式链接改为普通链接），Remove 去掉链接，Open 打开文件或外部地址。
// Link commands (design section 5.7): Apply sets the address (a reference link becomes a plain link), Remove drops
// the link, Open opens a project file or an external address.

export type CurrentLink = {
  readonly from: number;
  readonly to: number;
  /** 显示的地址；引用式链接由当前定义解析，没有定义时为 null。The shown address; a reference link resolves it from
   *  the current definitions, null without a definition. */
  readonly href: string | null;
  /** 引用式链接的标签。The label of a reference link. */
  readonly reference: string | null;
};

/** 光标（空选区）所在的链接。The link around the cursor (an empty selection). */
export function currentLink(state: EditorState): CurrentLink | null {
  return state.selection.empty ? linkAt(state, state.selection.from) : null;
}

/** `position` 处的链接。The link at `position`. */
export function linkAt(state: EditorState, position: number): CurrentLink | null {
  const { schema } = state;
  const range = getMarkRange(state.doc.resolve(position), schema.marks.link);
  if (!range) return null;
  const mark = state.doc
    .nodeAt(range.from)!
    .marks.find((candidate) => candidate.type === schema.marks.link)!;
  const reference = (mark.attrs.reference as string | null) ?? null;
  const href =
    reference === null
      ? String(mark.attrs.href ?? "")
      : (resolveReference(markdownDefinitionsKey.getState(state)!, reference)?.href ?? null);
  return { from: range.from, to: range.to, href, reference };
}

/** 包含整个选区的链接（空选区为光标所在的链接）。The link holding the whole selection (for an empty selection, the
 *  link around the cursor). */
export function selectionLink(state: EditorState): CurrentLink | null {
  const { from, to } = state.selection;
  const link = linkAt(state, from);
  return link !== null && link.from <= from && to <= link.to ? link : null;
}

/**
 * 把选区（或光标所在的链接）设为指向 `href` 的普通链接。选区在一个引用式链接中、`href` 等于它解析出的地址时保留引用
 * 写法，不做修改（与 updateImage 相同）。没有选中文字也不在链接中时不做任何事并返回 false。
 * Makes the selection (or the link around the cursor) a plain link to `href`. When the selection lies in a reference
 * link and `href` equals the address it resolves to, the reference spelling stays and nothing changes (as in
 * updateImage). Does nothing and returns false without selected text or a link around the cursor.
 */
export function applyLink(editor: Editor, href: string): boolean {
  if (editor.state.selection.empty && !currentLink(editor.state)) return false;
  const link = selectionLink(editor.state);
  if (link?.reference != null && link.href === href) return true;
  return editor
    .chain()
    .extendMarkRange("link")
    .setLink({ href })
    .updateAttributes("link", { reference: null, referenceKind: null })
    .run();
}

/** 去掉选区或光标所在的链接。Removes the link of the selection or around the cursor. */
export function removeLink(editor: Editor): boolean {
  return editor.chain().extendMarkRange("link").unsetLink().run();
}

/**
 * 打开链接：相对路径调用 `workspace.openFile`，`http`、`https` 调用 `ui.openExternal`，其他地址不响应（规则与
 * markdown-links.ts 相同）。返回是否打开。
 * Opens a link: relative paths call `workspace.openFile`, `http` and `https` call `ui.openExternal`, other addresses
 * do nothing (the rules of markdown-links.ts). Returns whether it opened.
 */
export function openLink(api: ExtensionApi, fileUri: string, href: string): boolean {
  const target = classifyLink(fileUri, href);
  if (target.kind === "file") notifyFailure(api, api.workspace.openFile(target.uri));
  else if (target.kind === "external") notifyFailure(api, api.ui.openExternal(target.url));
  else return false;
  return true;
}
