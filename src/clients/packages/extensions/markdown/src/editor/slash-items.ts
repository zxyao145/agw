import type { Editor, Range } from "@tiptap/core";
import { TextSelection, type EditorState } from "@tiptap/pm/state";

import { inTableCell, TURN_INTO_ITEMS, turnInto, type BlockType } from "./block-commands";
import type { EditUi } from "./edit-ui";

// `/` 命令菜单的项目（设计 5.7）。`range` 为输入的 `/` 与过滤文字，执行时先删除它。
// The `/` command menu's items (design section 5.7). `range` covers the typed `/` and filter text, which is deleted
// first.

export type SlashItem = {
  readonly title: string;
  run(editor: Editor, range: Range, ui: EditUi): void;
};

// HTML 块需要内容才能写成 Markdown，插入时带一个空的 div，光标放在标签之间。
// An HTML block needs content to be written as Markdown, so it starts as an empty div with the cursor between tags.
const HTML_BLOCK_OPEN = "<div>";
const HTML_BLOCK_TEXT = `${HTML_BLOCK_OPEN}</div>`;

function blockItem(title: string, type: BlockType): SlashItem {
  return {
    title,
    run: (editor, range) => {
      editor.commands.deleteRange(range);
      turnInto(editor, type);
    },
  };
}

/** 空 paragraph 换成 HTML 块，否则在当前顶层块之后插入。Replaces an empty paragraph by an HTML block, otherwise
 *  inserts it after the current top-level block. */
function insertHtmlBlock(editor: Editor, range: Range): void {
  editor.commands.deleteRange(range);
  const { state } = editor;
  const { $from } = state.selection;
  const block = state.schema.nodes.rawBlock.create(
    { kind: "html" },
    state.schema.text(HTML_BLOCK_TEXT),
  );
  const empty = $from.parent.type.name === "paragraph" && $from.parent.content.size === 0;
  const from = empty ? $from.before() : $from.after(1);
  const tr = state.tr.replaceWith(from, empty ? $from.after() : from, block);
  tr.setSelection(TextSelection.create(tr.doc, from + 1 + HTML_BLOCK_OPEN.length));
  editor.view.dispatch(tr);
}

export const SLASH_ITEMS: readonly SlashItem[] = [
  ...TURN_INTO_ITEMS.map((item) => blockItem(item.title, item.type)),
  {
    title: "Table",
    run: (editor, range) => {
      editor
        .chain()
        .deleteRange(range)
        .insertTable({ rows: 3, cols: 3, withHeaderRow: true })
        .run();
    },
  },
  {
    title: "Divider",
    run: (editor, range) => {
      editor.chain().deleteRange(range).setHorizontalRule().run();
    },
  },
  {
    title: "Image",
    run: (editor, range, ui) => {
      editor.commands.deleteRange(range);
      ui.requestImageInsert(editor.state.selection.from);
    },
  },
  { title: "HTML block", run: insertHtmlBlock },
];

// 表格单元格只能含一个 paragraph，其中只能插入图片。A table cell holds exactly one paragraph, where only an image can
// be inserted.
const IN_TABLE_CELL = new Set(["Image"]);

/**
 * 当前选区能使用的项目，按输入的文字过滤（不区分大小写）。
 * The items the current selection can use, filtered by the typed text (ignoring case).
 */
export function filterSlashItems(query: string, state: EditorState): SlashItem[] {
  const text = query.toLowerCase();
  const cell = inTableCell(state);
  return SLASH_ITEMS.filter(
    (item) => (!cell || IN_TABLE_CELL.has(item.title)) && item.title.toLowerCase().includes(text),
  );
}
