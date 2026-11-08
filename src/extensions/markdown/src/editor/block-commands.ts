import type { Editor } from "@tiptap/core";
import type { Node as ProseMirrorNode } from "@tiptap/pm/model";
import { TextSelection, type EditorState } from "@tiptap/pm/state";

// 块的命令（设计 5.7）：格式工具条与块菜单的 Turn into、块菜单的 Duplicate 与 Delete。`/` 菜单的块类型也使用 turnInto。
// Block commands (design section 5.7): Turn into for the formatting toolbar and the block menu, Duplicate and Delete
// for the block menu. The `/` menu's block types use turnInto too.

export type BlockType =
  | "paragraph"
  | "heading1"
  | "heading2"
  | "heading3"
  | "bulletList"
  | "orderedList"
  | "taskList"
  | "blockquote"
  | "codeBlock";

export const TURN_INTO_ITEMS: readonly { readonly title: string; readonly type: BlockType }[] = [
  { title: "Text", type: "paragraph" },
  { title: "Heading 1", type: "heading1" },
  { title: "Heading 2", type: "heading2" },
  { title: "Heading 3", type: "heading3" },
  { title: "Bulleted list", type: "bulletList" },
  { title: "Numbered list", type: "orderedList" },
  { title: "To-do list", type: "taskList" },
  { title: "Quote", type: "blockquote" },
  { title: "Code block", type: "codeBlock" },
];

const LIST_TYPES = new Set(["bulletList", "orderedList"]);
const HEADING_LEVELS: Partial<Record<BlockType, 1 | 2 | 3>> = {
  heading1: 1,
  heading2: 2,
  heading3: 3,
};

/** 选区所在的最内层列表。The innermost list around the selection. */
function innermostList(state: EditorState): { node: ProseMirrorNode; depth: number } | null {
  const { $from } = state.selection;
  for (let depth = $from.depth; depth > 0; depth -= 1) {
    const node = $from.node(depth);
    if (LIST_TYPES.has(node.type.name)) return { node, depth };
  }
  return null;
}

/** 选区所在的最内层列表项或引用的名称。The innermost list item or quote around the selection. */
function innermostWrapper(state: EditorState): "listItem" | "blockquote" | null {
  const { $from } = state.selection;
  for (let depth = $from.depth; depth > 0; depth -= 1) {
    const name = $from.node(depth).type.name;
    if (name === "listItem" || name === "blockquote") return name;
  }
  return null;
}

/** 把选区所在的块移出列表与引用。Moves the selected block out of lists and quotes. */
function unwrap(editor: Editor): void {
  for (
    let wrapper = innermostWrapper(editor.state);
    wrapper;
    wrapper = innermostWrapper(editor.state)
  ) {
    const lifted =
      wrapper === "listItem"
        ? editor.commands.liftListItem("listItem")
        : editor.commands.lift("blockquote");
    if (!lifted) throw new Error(`The block cannot be moved out of the ${wrapper}.`);
  }
}

/** 选区所在的最内层列表中、与选区相交的列表项的 `checked`。Sets `checked` on the items of the innermost list that the
 *  selection touches. */
function setChecked(editor: Editor, checked: boolean | null): void {
  const { state } = editor;
  const list = innermostList(state);
  if (!list) throw new Error("The selection is not in a list.");
  const { from, to } = state.selection;
  const tr = state.tr;
  let pos = state.selection.$from.start(list.depth);
  list.node.forEach((item) => {
    const end = pos + item.nodeSize;
    const touched = end > from && pos <= to;
    if (touched && (item.attrs.checked === null) !== (checked === null)) {
      tr.setNodeMarkup(pos, undefined, { ...item.attrs, checked });
    }
    pos = end;
  });
  if (tr.docChanged) editor.view.dispatch(tr);
}

/**
 * 把选区所在的块改为 `type`。列表之间直接切换；其他类型先移出列表与引用。
 * Turns the selected block into `type`. Lists switch in place; other types first leave lists and quotes.
 */
export function turnInto(editor: Editor, type: BlockType): void {
  // `editor.commands` 绑定读取时的状态，每个命令都重新读取。`editor.commands` binds the state at access time, so every
  // command reads it again.
  if (type === "bulletList" || type === "orderedList" || type === "taskList") {
    const target = type === "orderedList" ? "orderedList" : "bulletList";
    const list = innermostList(editor.state);
    if (!list) {
      editor.commands.setParagraph();
      if (!editor.commands.toggleList(target, "listItem")) {
        throw new Error(`The block cannot become a ${target}.`);
      }
    } else if (list.node.type.name !== target) {
      editor.commands.toggleList(target, "listItem");
    }
    setChecked(editor, type === "taskList" ? false : null);
    return;
  }
  unwrap(editor);
  const level = HEADING_LEVELS[type];
  if (level !== undefined) editor.commands.setHeading({ level });
  else if (type === "codeBlock") editor.commands.setCodeBlock();
  else {
    editor.commands.setParagraph();
    if (type === "blockquote" && !editor.commands.wrapIn("blockquote")) {
      throw new Error("The block cannot become a quote.");
    }
  }
}

/** 选区在表格单元格中（单元格只能含一个 paragraph）。Whether the selection is in a table cell (a cell holds exactly
 *  one paragraph). */
export function inTableCell(state: EditorState): boolean {
  const { $from } = state.selection;
  for (let depth = $from.depth; depth > 0; depth -= 1) {
    const name = $from.node(depth).type.name;
    if (name === "tableCell" || name === "tableHeader") return true;
  }
  return false;
}

/** 选区所在的块可以使用 Turn into：表格单元格中不能。Whether Turn into applies to the selected block; not in table
 *  cells. */
export function canTurnIntoSelection(state: EditorState): boolean {
  return !inTableCell(state);
}

/** 块菜单可以对这个节点使用 Turn into；front matter 不能。Whether the block menu offers Turn into for this node; not
 *  for front matter. */
export function canTurnInto(node: ProseMirrorNode): boolean {
  if (node.type.name === "frontMatter") return false;
  return node.isTextblock || ["listItem", "blockquote"].includes(node.type.name);
}

/** 块菜单可以复制这个节点：doc 中最多只有一个 front matter。Whether the block menu offers Duplicate for this node: a
 *  doc holds at most one front matter. */
export function canDuplicate(node: ProseMirrorNode): boolean {
  return node.type.name !== "frontMatter";
}

/** 把光标放进 `position` 处的块，供 Turn into 使用。Puts the cursor into the block at `position` for Turn into. */
export function selectBlock(editor: Editor, position: number): void {
  const { state } = editor;
  editor.view.dispatch(state.tr.setSelection(TextSelection.near(state.doc.resolve(position + 1))));
}

/** 在 `position` 处的块之后插入它的副本。Inserts a copy of the block at `position` after it. */
export function duplicateBlock(editor: Editor, position: number): void {
  const { state } = editor;
  const node = state.doc.nodeAt(position);
  if (!node || !canDuplicate(node))
    throw new Error(`The block at ${position} cannot be duplicated.`);
  editor.view.dispatch(state.tr.insert(position + node.nodeSize, node.copy(node.content)));
}

/** 删除 `position` 处的块；列表的最后一项连同列表一起删除。Deletes the block at `position`; a list's only item
 *  goes with its list. */
export function deleteBlock(editor: Editor, position: number): void {
  const { state } = editor;
  const node = state.doc.nodeAt(position);
  if (!node) throw new Error(`No block at ${position}.`);
  const $position = state.doc.resolve(position);
  const whole = node.type.name === "listItem" && $position.parent.childCount === 1;
  const from = whole ? $position.before() : position;
  const to = whole ? $position.after() : position + node.nodeSize;
  editor.view.dispatch(state.tr.delete(from, to));
}
