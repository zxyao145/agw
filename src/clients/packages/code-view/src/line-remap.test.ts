import assert from "node:assert/strict";
import test from "node:test";

import { remapLine } from "./line-remap";

test("remapLine_InsertedLinesAbove_ShiftsTheLineDown", () => {
  // 在第 3 行中间按两次 Enter。Enter pressed twice inside line 3.
  const changes = [{ startLineNumber: 3, endLineNumber: 3, insertedLineCount: 2 }];
  assert.equal(remapLine(2, changes), 2);
  assert.equal(remapLine(3, changes), 3);
  assert.equal(remapLine(4, changes), 6);
  assert.equal(remapLine(10, changes), 12);
});

test("remapLine_DeletedLinesAbove_ShiftsTheLineUp", () => {
  // 删除第 2 行到第 5 行之间的换行，四行合并为一行。The breaks between lines 2 and 5 are deleted, joining four lines.
  const changes = [{ startLineNumber: 2, endLineNumber: 5, insertedLineCount: 0 }];
  assert.equal(remapLine(1, changes), 1);
  assert.equal(remapLine(6, changes), 3);
  assert.equal(remapLine(9, changes), 6);
});

test("remapLine_LineInsideTheReplacedRange_MovesWithinTheInsertedText", () => {
  // 第 4 行到第 8 行替换为三行文字。Lines 4 to 8 are replaced by three lines of text.
  const changes = [{ startLineNumber: 4, endLineNumber: 8, insertedLineCount: 2 }];
  assert.equal(remapLine(5, changes), 5);
  assert.equal(remapLine(6, changes), 6);
  // 插入的文字只有三行，后面的行停在最后一行。The inserted text has three lines, so later lines stop at its last line.
  assert.equal(remapLine(7, changes), 6);
  assert.equal(remapLine(8, changes), 6);
  assert.equal(remapLine(9, changes), 7);
});

test("remapLine_SeveralChangesFromEndToStart_AppliesEachInTurn", () => {
  // 多光标：第 10 行插入一行，第 2 行插入两行，顺序为从文档末尾到开头。Multiple cursors: one line at line 10, two lines
  // at line 2, ordered from the end of the document to the start.
  const changes = [
    { startLineNumber: 10, endLineNumber: 10, insertedLineCount: 1 },
    { startLineNumber: 2, endLineNumber: 2, insertedLineCount: 2 },
  ];
  assert.equal(remapLine(5, changes), 7);
  assert.equal(remapLine(11, changes), 14);
  assert.equal(remapLine(1, changes), 1);
});

test("remapLine_EditWithinOneLine_KeepsEveryLine", () => {
  const changes = [{ startLineNumber: 7, endLineNumber: 7, insertedLineCount: 0 }];
  for (const line of [1, 7, 8, 30]) assert.equal(remapLine(line, changes), line);
});
