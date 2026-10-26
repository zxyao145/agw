// 内容变化后的行号换算（设计文档第 6 节）：评论与正在新增评论的行跟随代码的编辑移动。这个模块不导入 Monaco。
// Line number remapping after content changes (design section 6): comments and the line getting a new comment follow
// the code edits. This module does not import Monaco.

/**
 * 一次内容变化中的一个改动：被替换的行范围（改动之前的行号）与插入文字中的换行数。
 * One change of a content change: the replaced line range (line numbers before the change) and the number of line
 * breaks in the inserted text.
 */
export interface LineChange {
  startLineNumber: number;
  endLineNumber: number;
  insertedLineCount: number;
}

/**
 * 内容变化之后 `line` 的行号。`changes` 按 Monaco 给出的顺序（从文档末尾到开头）依次处理：行号不大于改动的起始行时不变；
 * 大于结束行时加上增加的行数；在改动范围内时移到插入文字中对应的行，插入的行数不够时停在插入文字的最后一行。
 * The line number of `line` after a content change. `changes` are processed in Monaco's order (from the end of the
 * document to the start): a line at or before a change's start stays; a line after its end shifts by the added lines;
 * a line inside the range moves to the matching line of the inserted text, or its last line when it has fewer lines.
 */
export function remapLine(line: number, changes: readonly LineChange[]): number {
  let current = line;
  for (const change of changes) {
    if (current <= change.startLineNumber) continue;
    if (current > change.endLineNumber) {
      current += change.insertedLineCount - (change.endLineNumber - change.startLineNumber);
    } else {
      current =
        change.startLineNumber +
        Math.min(current - change.startLineNumber, change.insertedLineCount);
    }
  }
  return current;
}
