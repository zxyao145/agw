import type { Editor } from "@tiptap/core";
import { TableMap } from "@tiptap/pm/tables";

// 表格工具条的操作（设计 5.7）。插入行之后的单元格类型与对齐方式由 table-normalization 补齐。
// The table toolbar's operations (design section 5.7). table-normalization fixes cell types and alignment after rows
// are inserted.

export type TableAlign = "left" | "center" | "right";

/** 把光标所在列的全部单元格设为 `align`。Sets every cell of the cursor's column to `align`. */
function alignColumn(editor: Editor, align: TableAlign): boolean {
  const { state } = editor;
  const { $from } = state.selection;
  for (let depth = $from.depth; depth > 0; depth -= 1) {
    if ($from.node(depth).type.name !== "table") continue;
    const table = $from.node(depth);
    const tableStart = $from.start(depth);
    const map = TableMap.get(table);
    const column = map.colCount($from.before(depth + 2) - tableStart);
    const tr = state.tr;
    for (let row = 0; row < map.height; row += 1) {
      const offset = map.map[row * map.width + column];
      const cell = table.nodeAt(offset)!;
      tr.setNodeMarkup(tableStart + offset, undefined, { ...cell.attrs, align });
    }
    editor.view.dispatch(tr);
    return true;
  }
  return false;
}

export const TABLE_ACTIONS: readonly { readonly title: string; run(editor: Editor): boolean }[] = [
  { title: "Insert row above", run: (editor) => editor.commands.addRowBefore() },
  { title: "Insert row below", run: (editor) => editor.commands.addRowAfter() },
  { title: "Insert column left", run: (editor) => editor.commands.addColumnBefore() },
  { title: "Insert column right", run: (editor) => editor.commands.addColumnAfter() },
  { title: "Delete row", run: (editor) => editor.commands.deleteRow() },
  { title: "Delete column", run: (editor) => editor.commands.deleteColumn() },
  { title: "Align left", run: (editor) => alignColumn(editor, "left") },
  { title: "Align center", run: (editor) => alignColumn(editor, "center") },
  { title: "Align right", run: (editor) => alignColumn(editor, "right") },
  { title: "Delete table", run: (editor) => editor.commands.deleteTable() },
];
