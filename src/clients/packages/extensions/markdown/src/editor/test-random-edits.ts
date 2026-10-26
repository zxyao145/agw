// 测试用：随机编辑（设计“测试”中的随机编辑）。使用编辑器自身的事务与命令；每个操作带一组 [0, 1) 的数，执行时按当前文档
// 选择目标。
// For tests: random edits (the random editing case of the design's tests). Real editor transactions and commands;
// each operation carries numbers in [0, 1) and picks its target from the current document when it runs.
import assert from "node:assert/strict";

import type { Editor, JSONContent } from "@tiptap/core";
import type { Node as ProseMirrorNode } from "@tiptap/pm/model";
import fc from "fast-check";

import { createSpellingBudget } from "../markdown/markdown-inline";
import { comparableDocument } from "./document-equivalence";
import type { MarkdownBaseline } from "./markdown-loader";
import {
  composeMarkdown,
  MarkdownRoundTripError,
  verifyComposition,
  writeMarkdown,
} from "./markdown-writer";
import { openMarkdown } from "./test-editor";

const ALPHABET = "abc XYZ 12.,!?*_[]#>-+|\\:/()&<~`";
const MARKS = ["bold", "italic", "strike", "code"] as const;
const BLOCK_TYPES = [
  "paragraph",
  "heading",
  "codeBlock",
  "bulletList",
  "orderedList",
  "blockquote",
] as const;

export type Operation =
  | { kind: "insertText"; at: number; offset: number; text: string }
  | { kind: "deleteText"; at: number; from: number; length: number }
  | {
      kind: "toggleMark";
      at: number;
      from: number;
      length: number;
      mark: (typeof MARKS)[number] | "link";
    }
  | { kind: "setBlockType"; at: number; type: (typeof BLOCK_TYPES)[number]; level: number }
  | { kind: "moveBlock"; at: number; to: number }
  | { kind: "deleteBlock"; at: number }
  | { kind: "toggleTask"; at: number }
  | { kind: "editRawBlock"; at: number; text: string }
  | { kind: "editDefinition"; at: number; path: number }
  | { kind: "insertCode"; at: number; offset: number; text: string; marks: EnclosingMark[] };

// 可以包住行内代码的标记（例外 (b) 的条件）。Marks that can enclose inline code (the condition of exception (b)).
const ENCLOSING_MARKS = ["link", "bold", "italic", "strike"] as const;
type EnclosingMark = (typeof ENCLOSING_MARKS)[number];

const unit = fc.double({ min: 0, max: 1, maxExcluded: true, noNaN: true });
const text = fc.string({
  unit: fc.constantFrom(...ALPHABET.split("")),
  minLength: 1,
  maxLength: 6,
});
// 行内代码的文字：常含反引号、分隔符字符与 `\|`，使两种例外与能写出的情况都经常出现。Inline code text that often holds
// backticks, delimiter characters and `\|`, so both exceptions and their writable neighbours come up often.
const codeText = fc.oneof(
  fc.constantFrom("a`b", "`", "x\\|y", "a`*b", "a`_b", "a`*_b", "a`~~b", "a`~b~~"),
  fc.string({ unit: fc.constantFrom(..."ab`*_~\\| "), minLength: 1, maxLength: 5 }),
);

export const operationArbitrary: fc.Arbitrary<Operation> = fc.oneof(
  fc.record({ kind: fc.constant("insertText" as const), at: unit, offset: unit, text }),
  fc.record({ kind: fc.constant("deleteText" as const), at: unit, from: unit, length: unit }),
  fc.record({
    kind: fc.constant("toggleMark" as const),
    at: unit,
    from: unit,
    length: unit,
    mark: fc.constantFrom(...MARKS, "link" as const),
  }),
  fc.record({
    kind: fc.constant("setBlockType" as const),
    at: unit,
    type: fc.constantFrom(...BLOCK_TYPES),
    level: fc.integer({ min: 1, max: 3 }),
  }),
  fc.record({ kind: fc.constant("moveBlock" as const), at: unit, to: unit }),
  fc.record({ kind: fc.constant("deleteBlock" as const), at: unit }),
  fc.record({ kind: fc.constant("toggleTask" as const), at: unit }),
  fc.record({
    kind: fc.constant("editRawBlock" as const),
    at: unit,
    text: fc.string({ unit: fc.constantFrom(..."abcxyz"), minLength: 1, maxLength: 4 }),
  }),
  fc.record({
    kind: fc.constant("editDefinition" as const),
    at: unit,
    path: fc.integer({ min: 0, max: 99 }),
  }),
  {
    weight: 3,
    arbitrary: fc.record({
      kind: fc.constant("insertCode" as const),
      at: unit,
      offset: unit,
      text: codeText,
      marks: fc.subarray([...ENCLOSING_MARKS]),
    }),
  },
);

type Located = { node: ProseMirrorNode; pos: number };

function collect(editor: Editor, predicate: (node: ProseMirrorNode) => boolean): Located[] {
  const found: Located[] = [];
  editor.state.doc.descendants((node, pos) => {
    if (predicate(node)) found.push({ node, pos });
  });
  return found;
}

function pick<T>(items: readonly T[], fraction: number): T | undefined {
  return items.length ? items[Math.floor(fraction * items.length)] : undefined;
}

// 可以输入文字的块：paragraph、标题与代码块（原文块与 front matter 由专门的操作编辑）。
// Blocks that take typed text: paragraphs, headings and code blocks (raw blocks and front matter have their own
// operations).
function editableTextblock(node: ProseMirrorNode): boolean {
  return ["paragraph", "heading", "codeBlock"].includes(node.type.name);
}

function topLevelBlocks(editor: Editor): Located[] {
  const blocks: Located[] = [];
  editor.state.doc.forEach((node, offset) => {
    if (node.type.name !== "frontMatter") blocks.push({ node, pos: offset });
  });
  return blocks;
}

function applyOperation(editor: Editor, op: Operation): void {
  const { state } = editor;
  switch (op.kind) {
    case "insertText": {
      const target = pick(collect(editor, editableTextblock), op.at);
      if (!target) return;
      const position = target.pos + 1 + Math.floor(op.offset * (target.node.content.size + 1));
      editor.view.dispatch(state.tr.insertText(op.text, position));
      return;
    }
    case "deleteText": {
      const target = pick(collect(editor, editableTextblock), op.at);
      if (!target || !target.node.content.size) return;
      const size = target.node.content.size;
      const from = Math.floor(op.from * size);
      const to = Math.min(size, from + 1 + Math.floor(op.length * (size - from)));
      editor.view.dispatch(state.tr.delete(target.pos + 1 + from, target.pos + 1 + to));
      return;
    }
    case "toggleMark": {
      const target = pick(
        collect(
          editor,
          (node) => ["paragraph", "heading"].includes(node.type.name) && node.content.size > 0,
        ),
        op.at,
      );
      if (!target) return;
      const size = target.node.content.size;
      const from = target.pos + 1 + Math.floor(op.from * size);
      const to = Math.min(target.pos + 1 + size, from + 1 + Math.floor(op.length * size));
      const chain = editor.chain().setTextSelection({ from, to });
      if (op.mark === "link") {
        chain.toggleMark("link", { href: "/linked" }).run();
      } else {
        chain.toggleMark(op.mark).run();
      }
      return;
    }
    case "setBlockType": {
      const target = pick(
        topLevelBlocks(editor).filter(({ node }) =>
          ["paragraph", "heading"].includes(node.type.name),
        ),
        op.at,
      );
      if (!target) return;
      const chain = editor.chain().setTextSelection(target.pos + 1);
      if (op.type === "paragraph") chain.setParagraph().run();
      else if (op.type === "heading") chain.setHeading({ level: op.level as 1 | 2 | 3 }).run();
      else if (op.type === "codeBlock") chain.toggleCodeBlock().run();
      else if (op.type === "bulletList") chain.toggleBulletList().run();
      else if (op.type === "orderedList") chain.toggleOrderedList().run();
      else chain.toggleBlockquote().run();
      return;
    }
    case "moveBlock": {
      const blocks = topLevelBlocks(editor);
      const target = pick(blocks, op.at);
      if (!target || blocks.length < 2) return;
      const tr = state.tr.delete(target.pos, target.pos + target.node.nodeSize);
      const positions: number[] = [];
      tr.doc.forEach((node, offset) => {
        if (node.type.name !== "frontMatter") positions.push(offset);
      });
      positions.push(tr.doc.content.size);
      tr.insert(pick(positions, op.to)!, target.node);
      editor.view.dispatch(tr);
      return;
    }
    case "deleteBlock": {
      const blocks = topLevelBlocks(editor);
      const target = pick(blocks, op.at);
      if (!target || blocks.length < 2) return;
      editor.view.dispatch(state.tr.delete(target.pos, target.pos + target.node.nodeSize));
      return;
    }
    case "toggleTask": {
      const target = pick(
        collect(editor, (node) => node.type.name === "listItem" && node.attrs.checked !== null),
        op.at,
      );
      if (!target) return;
      editor.view.dispatch(
        state.tr.setNodeMarkup(target.pos, undefined, {
          ...target.node.attrs,
          checked: !target.node.attrs.checked,
        }),
      );
      return;
    }
    case "editRawBlock": {
      // 在 HTML 块或脚注定义的末尾输入文字，块仍是同一种原文块。Typing at the end of an HTML block or footnote
      // definition keeps it the same kind of raw block.
      const target = pick(
        collect(
          editor,
          (node) => node.type.name === "rawBlock" && node.attrs.kind !== "definition",
        ),
        op.at,
      );
      if (!target) return;
      editor.view.dispatch(state.tr.insertText(op.text, target.pos + 1 + target.node.content.size));
      return;
    }
    case "editDefinition": {
      const target = pick(
        collect(
          editor,
          (node) => node.type.name === "rawBlock" && node.attrs.kind === "definition",
        ),
        op.at,
      );
      if (!target) return;
      const label = /^\s*\[([^\]]+)\]:/.exec(target.node.textContent)![1];
      editor.view.dispatch(
        state.tr.insertText(
          `[${label}]: /changed/${op.path}`,
          target.pos + 1,
          target.pos + 1 + target.node.content.size,
        ),
      );
      return;
    }
    case "insertCode": {
      // 在 paragraph（包括表格单元格中的）或标题中插入一段行内代码，外面带上选中的标记。Inserts inline code into a
      // paragraph (table cells included) or heading, enclosed by the chosen marks.
      const target = pick(
        collect(editor, (node) => ["paragraph", "heading"].includes(node.type.name)),
        op.at,
      );
      if (!target) return;
      const { schema } = state;
      const marks = [
        schema.marks.code.create(),
        ...op.marks.map((name) =>
          name === "link"
            ? schema.marks.link.create({ href: "/linked" })
            : schema.marks[name].create(),
        ),
      ];
      const position = target.pos + 1 + Math.floor(op.offset * (target.node.content.size + 1));
      editor.view.dispatch(state.tr.insert(position, schema.text(op.text, marks)));
      return;
    }
  }
}

const TABLE_CELLS = new Set(["tableCell", "tableHeader"]);

type ExceptionText = { node: ProseMirrorNode; pos: number; kind: "a" | "b" };

/**
 * 设计规定的两种例外（marked 读不回时保存按设计以 MarkdownRoundTripError 失败），返回 `root` 中这样的行内代码文字：
 * (a) 表格单元格中含有“反斜杠加 `|`”的行内代码：marked 的 splitCells 把 `\|` 中的 `|` 当作单元格分隔或去掉它前面的
 *     反斜杠，没有写法能读回 `\|`。
 * (b) 链接、bold、斜体或删除线中含有反引号的行内代码：marked 寻找强调范围与链接文字时只跳过不含反引号的行内代码，
 *     代码中的字符会被当作分隔符或括号。写出时会尝试其他分隔符与代码分隔符长度（见 markdown-inline.ts 的
 *     writeInline），只有都不能读回时保存才失败。
 * The two exceptions the design allows (when marked cannot read them back, saving fails with MarkdownRoundTripError
 * as designed); returns such inline code texts in `root`:
 * (a) Inline code holding a backslash followed by `|` in a table cell: marked's splitCells treats the `|` of `\|` as
 *     a cell separator or drops the backslash before it, and no spelling reads `\|` back.
 * (b) Inline code holding a backtick inside a link, bold, italic or strikethrough: marked skips only backtick-free
 *     inline code when it looks for emphasis ranges and link text, so characters inside the code count as delimiters
 *     or brackets. Writing tries the other delimiters and code fence lengths (writeInline in markdown-inline.ts), and
 *     saving fails only when none of them reads back.
 */
function exceptionTexts(root: ProseMirrorNode): ExceptionText[] {
  const found: ExceptionText[] = [];
  root.descendants((node, pos) => {
    if (!node.isText || !node.marks.some((mark) => mark.type.name === "code")) return;
    const text = node.text ?? "";
    const $pos = root.resolve(pos);
    let inTableCell = false;
    for (let depth = $pos.depth; depth >= 0; depth--) {
      inTableCell ||= TABLE_CELLS.has($pos.node(depth).type.name);
    }
    const enclosed = node.marks.some((mark) =>
      (ENCLOSING_MARKS as readonly string[]).includes(mark.type.name),
    );
    if (inTableCell && text.includes("\\|")) found.push({ node, pos, kind: "a" });
    else if (enclosed && text.includes("`")) found.push({ node, pos, kind: "b" });
  });
  return found;
}

/**
 * 失败发生在例外所在的地方：内容不同的失败（对比坐标中的顶层块 `path[0]`）所在的块含有例外；无法载入的失败只接受
 * 表格单元格中含有 `\|` 的行内代码（例外 (a)）。
 * The failure occurs where an exception is: a content difference whose top-level block (`path[0]` in comparison
 * coordinates) holds an exception, or a load failure while a table cell holds inline code with `\|` (exception (a)).
 */
function failsAtException(error: MarkdownRoundTripError, doc: ProseMirrorNode): boolean {
  const { failure } = error;
  if (failure.kind !== "content") return false;
  if (!failure.path.length) return exceptionTexts(doc).some((item) => item.kind === "a");
  const expected = comparableDocument(doc.toJSON() as JSONContent, doc.type.schema);
  const block = expected.maybeChild(failure.path[0]);
  return block !== null && exceptionTexts(block).length > 0;
}

/** 不限读回检查的量时保存（组合与两项校验）的失败；能保存时为 null。The save's failure (composition and both
 *  verifications) without a read-back limit; null when it saves. */
function failureWithoutLimit(
  baseline: MarkdownBaseline,
  doc: ProseMirrorNode,
): MarkdownRoundTripError | null {
  try {
    verifyComposition(
      baseline,
      doc,
      composeMarkdown(baseline, doc, createSpellingBudget(Number.POSITIVE_INFINITY)),
    );
    return null;
  } catch (error) {
    if (!(error instanceof MarkdownRoundTripError)) throw error;
    return error;
  }
}

/**
 * 保存的失败可以按例外接受：它是发生在例外所在地方的 MarkdownRoundTripError（failsAtException），并且不限读回检查的
 * 量时同一文档仍在例外所在的地方失败，失败与写法搜索的限制无关。
 * A save failure can be accepted as an exception: it is a MarkdownRoundTripError where an exception is
 * (failsAtException), and without a read-back limit the same document still fails where an exception is, so the
 * failure does not come from the spelling search's limit.
 */
export function isExceptionFailure(
  baseline: MarkdownBaseline,
  doc: ProseMirrorNode,
  error: unknown,
): boolean {
  if (!(error instanceof MarkdownRoundTripError) || !failsAtException(error, doc)) return false;
  const unlimited = failureWithoutLimit(baseline, doc);
  return unlimited !== null && failsAtException(unlimited, doc);
}

/** 去掉例外：代码中的反引号改为 `'`，`\|` 改为 `/|`。Removes the exceptions: backticks in the code become `'` and
 *  `\|` becomes `/|`. */
function withoutExceptions(editor: Editor): void {
  const tr = editor.state.tr;
  for (const { node, pos } of exceptionTexts(editor.state.doc).reverse()) {
    const text = (node.text ?? "").replaceAll("`", "'").replaceAll("\\|", "/|");
    tr.replaceWith(pos, pos + node.nodeSize, editor.schema.text(text, node.marks));
  }
  editor.view.dispatch(tr);
}

export type SaveOutcome = {
  readonly outcome: "unchanged" | "saved" | "failedSafely";
  readonly holdsException: boolean;
};

/** 保存后的基线；没有写入时换成当前文档（与 edit-document.ts 相同）。The baseline after a save; without a write it
 *  takes the current document (as edit-document.ts does). */
function nextBaseline(
  baseline: MarkdownBaseline,
  editor: Editor,
  result: ReturnType<typeof writeMarkdown>,
): MarkdownBaseline {
  return result ? result.baseline : { ...baseline, doc: editor.state.doc };
}

/**
 * 保存一次；两项校验在 writeMarkdown 中，另外检查每个保留块的原文都在写出的文本中。保存以 MarkdownRoundTripError
 * 失败时，只在它可以按例外接受（isExceptionFailure），并且去掉例外后保存成功（失败由例外引起）时接受，之后以去掉例外
 * 后的那次保存为准；其他错误与其他失败都使测试失败。
 * Saves once; both verifications run inside writeMarkdown, and every preserved block's source must appear in the
 * written text. A save failing with MarkdownRoundTripError is accepted only when it can be accepted as an exception
 * (isExceptionFailure) and the save succeeds once the exceptions are removed (the exception caused it); that save
 * then counts. Any other error or failure fails the test.
 */
function saveOnce(
  editor: Editor,
  baseline: MarkdownBaseline,
): { outcome: SaveOutcome; baseline: MarkdownBaseline } {
  const holdsException = exceptionTexts(editor.state.doc).length > 0;
  let result: ReturnType<typeof writeMarkdown>;
  try {
    result = writeMarkdown(baseline, editor.state.doc);
  } catch (error) {
    if (!isExceptionFailure(baseline, editor.state.doc, error)) throw error;
    withoutExceptions(editor);
    let saved: ReturnType<typeof writeMarkdown>;
    try {
      saved = writeMarkdown(baseline, editor.state.doc);
    } catch (other) {
      throw new Error(`The save still fails without the exceptions: ${String(other)}`, {
        cause: error,
      });
    }
    return {
      outcome: { outcome: "failedSafely", holdsException },
      baseline: nextBaseline(baseline, editor, saved),
    };
  }
  if (result) {
    for (const span of composeMarkdown(baseline, editor.state.doc).preserved) {
      assert.ok(result.text.includes(baseline.source.slice(span.source.from, span.source.to)));
    }
  }
  return {
    outcome: { outcome: result ? "saved" : "unchanged", holdsException },
    baseline: nextBaseline(baseline, editor, result),
  };
}

/**
 * 在同一个编辑器中逐轮执行编辑，每一轮之后保存一次（saveOnce）；之后的保存以上一次保存后的基线为准，与 Mixed 的文档
 * 会话相同。返回每次保存的结果。
 * Runs the edits round by round in one editor and saves after each round (saveOnce); each later save uses the
 * baseline after the previous save, as Mixed's document session does. Returns every save's outcome.
 */
export function editAndSave(
  source: string,
  rounds: readonly (readonly Operation[])[],
): SaveOutcome[] {
  const loaded = openMarkdown(source);
  const { editor } = loaded;
  let { baseline } = loaded;
  const outcomes: SaveOutcome[] = [];
  try {
    for (const operations of rounds) {
      for (const op of operations) applyOperation(editor, op);
      const saved = saveOnce(editor, baseline);
      outcomes.push(saved.outcome);
      baseline = saved.baseline;
    }
    return outcomes;
  } finally {
    editor.destroy();
  }
}
