import { Extension } from "@tiptap/core";
import type { Node as ProseMirrorNode } from "@tiptap/pm/model";
import { Plugin, PluginKey, type Transaction } from "@tiptap/pm/state";
import { TableMap } from "@tiptap/pm/tables";

import { changedBlockRanges } from "./changed-ranges";

// 表格保持 Markdown 能写出的形状：第一行是标题行（tableHeader），其余行是 tableCell；同一列的单元格对齐方式相同，因为
// Markdown 的对齐写在分隔行中、作用于整列。插入行、删除标题行或按 Tab 新增的行之后由这个插件补齐。
// Tables keep a shape Markdown can write: the first row is the header row (tableHeader) and the other rows are
// tableCell; every cell of a column has the same alignment, because Markdown writes alignment in the delimiter row
// for the whole column. This plugin restores that after inserting rows, deleting the header row or adding a row with
// Tab.

type CellFix = { pos: number; type: ProseMirrorNode["type"]; align: unknown };

/** 一个表格需要修改的单元格；`tableStart` 为表格内容的开始位置。The cells of one table to fix; `tableStart` is where
 *  the table's content starts. */
function tableFixes(table: ProseMirrorNode, tableStart: number): CellFix[] {
  const { schema } = table.type;
  const map = TableMap.get(table);
  const cells = (row: number) =>
    Array.from({ length: map.width }, (_, column) => {
      const offset = map.map[row * map.width + column];
      return { offset, node: table.nodeAt(offset)! };
    });
  const columnAlign = Array.from({ length: map.width }, (_, column) => {
    const values = Array.from(
      { length: map.height },
      (_, row) => table.nodeAt(map.map[row * map.width + column])!.attrs.align as unknown,
    );
    const set = new Set(values.filter((value) => value !== null));
    // 新增的单元格没有对齐方式：其余单元格一致时沿用它们的对齐方式，否则以标题行为准。
    // New cells have no alignment: they take the others' alignment when that is uniform, otherwise the header row's.
    return set.size === 1 ? [...set][0] : values[0];
  });
  const fixes: CellFix[] = [];
  for (let row = 0; row < map.height; row += 1) {
    const type = row === 0 ? schema.nodes.tableHeader : schema.nodes.tableCell;
    for (const [column, { offset, node }] of cells(row).entries()) {
      if (node.type !== type || node.attrs.align !== columnAlign[column]) {
        fixes.push({ pos: tableStart + offset, type, align: columnAlign[column] });
      }
    }
  }
  return fixes;
}

function normalizeTables(
  transactions: readonly Transaction[],
  tr: Transaction,
): Transaction | null {
  const { doc } = tr;
  const fixes: CellFix[] = [];
  const visited = new Set<number>();
  const visit = (table: ProseMirrorNode, pos: number) => {
    if (visited.has(pos)) return;
    visited.add(pos);
    fixes.push(...tableFixes(table, pos + 1));
  };
  for (const range of changedBlockRanges(transactions, doc)) {
    doc.nodesBetween(range.from, range.to, (node, pos) => {
      if (node.type.name === "table") {
        visit(node, pos);
        return false;
      }
      return !node.isTextblock;
    });
  }
  if (!fixes.length) return null;
  for (const fix of fixes) {
    const cell = tr.doc.nodeAt(fix.pos)!;
    tr.setNodeMarkup(fix.pos, fix.type, { ...cell.attrs, align: fix.align });
  }
  return tr;
}

export const TableNormalization = Extension.create({
  name: "tableNormalization",
  addProseMirrorPlugins() {
    return [
      new Plugin({
        key: new PluginKey("tableNormalization"),
        appendTransaction: (transactions, _oldState, newState) =>
          transactions.some((transaction) => transaction.docChanged)
            ? normalizeTables(transactions, newState.tr)
            : null,
      }),
    ];
  },
});
