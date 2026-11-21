import assert from "node:assert/strict";
import test from "node:test";

import fc from "fast-check";

import { openMarkdown, readTestInputs, type LoadedEditor } from "./test-editor";

const { editAndSave, isExceptionFailure, operationArbitrary } = await import("./test-random-edits");
const { composeMarkdown, MarkdownRoundTripError, verifyComposition, writeMarkdown } =
  await import("./markdown-writer");
const { createSpellingBudget } = await import("../markdown/markdown-inline");

type Operations = Parameters<typeof editAndSave>[1][number];

const inputs = readTestInputs();

function input(name: string): string {
  return inputs.find((candidate) => candidate.name === name)!.source;
}

test("random edits of every input file, saved up to three times in one editor, pass both verifications except the two documented exceptions", () => {
  let savedWithException = 0;
  let failedSafely = 0;
  let laterSaves = 0;
  fc.assert(
    fc.property(
      fc.constantFrom(...inputs),
      fc.array(operationArbitrary, { minLength: 1, maxLength: 20 }),
      // 在这些位置把编辑分成几轮，每一轮之后保存一次。The edits are divided into rounds at these positions, with one
      // save after each round.
      fc.uniqueArray(fc.integer({ min: 1, max: 19 }), { maxLength: 2 }),
      (input, operations, cuts) => {
        const bounds = [0, ...cuts.filter((cut) => cut < operations.length).sort((a, b) => a - b)];
        const rounds = bounds.map((from, index) => operations.slice(from, bounds[index + 1]));
        for (const [index, { outcome, holdsException }] of editAndSave(
          input.source,
          rounds,
        ).entries()) {
          if (outcome === "saved" && holdsException) savedWithException++;
          if (outcome === "failedSafely") failedSafely++;
          if (index > 0 && outcome === "saved") laterSaves++;
        }
      },
    ),
    { numRuns: 300, seed: 20261019 },
  );
  assert.ok(laterSaves > 0, "no later save in one editor wrote anything");
  // 两条路径都要走到：含有例外写法但能写出的保存，以及按设计失败的保存。Both paths must be reached: saves holding an
  // exception construct that can be written, and saves that fail as designed.
  assert.ok(savedWithException > 0, "no save held an exception construct that could be written");
  assert.ok(failedSafely > 0, "no save failed safely at an exception");
});

// fast-check 缩小后的失败用例。Failing cases shrunk by fast-check.
const SHRUNK_CASES: [string, Operations][] = [
  // 行内代码标记加在脚注引用上。An inline code mark on a footnote reference.
  [
    "tiptap-samples.md",
    [
      { kind: "toggleMark", at: 0, from: 0.8333333333333333, length: 0, mark: "code" },
      { kind: "insertText", at: 0, offset: 0, text: "a" },
    ],
  ],
  // 含有 hardBreak 的 paragraph 改为 3 级标题。A paragraph with hard breaks turned into a level 3 heading.
  [
    "html-and-footnotes.md",
    [
      { kind: "setBlockType", at: 0, type: "heading", level: 3 },
      { kind: "toggleTask", at: 0 },
    ],
  ],
  // 删除第一行后 paragraph 以换行开始。A paragraph starting with a newline after its first line is deleted.
  [
    "tiptap-samples.md",
    [
      { kind: "deleteBlock", at: 0 },
      { kind: "deleteBlock", at: 0 },
      { kind: "deleteText", at: 0, from: 0, length: 0 },
    ],
  ],
  // 以标点结尾的 bold 紧挨删除线（marked 的 GFM 规则不把 `~` 当作标点）。Bold ending in punctuation right before
  // strikethrough (marked's GFM rules do not count `~` as punctuation).
  [
    "tiptap-samples.md",
    [
      { kind: "toggleMark", at: 0, from: 0, length: 0, mark: "strike" },
      { kind: "insertText", at: 0, offset: 0, text: "," },
      { kind: "toggleMark", at: 0, from: 0, length: 0, mark: "strike" },
      { kind: "toggleMark", at: 0, from: 0, length: 0, mark: "bold" },
    ],
  ],
  // 斜体与 bold 相邻、外面是删除线：`_` 与 `__` 连在一起会被读成一串。Italic next to bold inside strikethrough:
  // adjoining `_` and `__` would read as one run.
  [
    "html-and-footnotes.md",
    [
      { kind: "toggleMark", at: 0, from: 0, length: 0, mark: "bold" },
      { kind: "toggleMark", at: 0, from: 0, length: 0, mark: "strike" },
      { kind: "insertText", at: 0, offset: 0, text: "a" },
      { kind: "toggleMark", at: 0, from: 0, length: 0, mark: "bold" },
      { kind: "toggleMark", at: 0, from: 0, length: 0, mark: "italic" },
    ],
  ],
  // setext 标题的文字以 `#` 开头。Setext heading text starting with `#`.
  [
    "lists-and-emphasis.md",
    [
      { kind: "insertText", at: 0, offset: 0, text: "a#a" },
      { kind: "deleteText", at: 0, from: 0, length: 0 },
      { kind: "editDefinition", at: 0, path: 0 },
    ],
  ],
];

test("shrunk random edit failures save with both verifications passing", () => {
  for (const [name, operations] of SHRUNK_CASES) editAndSave(input(name), [operations]);
});

type Segment = [text: string, marks: string[]];

/** 把文字为 `target` 的文本块的内容换成 `segments`。Replaces the content of the textblock whose text is `target` with
 *  `segments`. */
function openSegments(source: string, target: string, segments: Segment[]): LoadedEditor {
  const loaded = openMarkdown(source);
  const { editor } = loaded;
  const { schema } = editor;
  let pos = -1;
  editor.state.doc.descendants((node, offset) => {
    if (pos < 0 && node.isTextblock && node.textContent === target) pos = offset;
  });
  const nodes = segments.map(([text, marks]) =>
    schema.text(
      text,
      marks.map((type) =>
        type === "link" ? schema.marks.link.create({ href: "/u" }) : schema.marks[type].create(),
      ),
    ),
  );
  editor.view.dispatch(
    editor.state.tr.replaceWith(
      pos + 1,
      pos + 1 + editor.state.doc.nodeAt(pos)!.content.size,
      nodes,
    ),
  );
  return loaded;
}

/** openSegments 之后保存；返回写出的文字，保存失败时抛出。Saves after openSegments; returns the written text and
 *  throws on failure. */
function saveSegments(source: string, target: string, segments: Segment[]): string | undefined {
  const { editor, baseline } = openSegments(source, target, segments);
  try {
    return writeMarkdown(baseline, editor.state.doc)?.text;
  } finally {
    editor.destroy();
  }
}

function roundTripError(run: () => unknown): InstanceType<typeof MarkdownRoundTripError> {
  let caught: unknown = null;
  assert.throws(run, (error) => {
    caught = error;
    return error instanceof MarkdownRoundTripError;
  });
  return caught as InstanceType<typeof MarkdownRoundTripError>;
}

test("a save failure counts as an exception only when it also fails without a spelling limit", () => {
  // 能写出的例外写法：读回检查的余量为 0 时只有默认写法，保存失败；余量不限时能保存，因此不按例外接受。
  // A writable exception construct: with a read-back limit of 0 only the default spelling is checked and the save
  // fails; without a limit it saves, so the failure is not accepted as an exception.
  const writable = openSegments("Text.\n", "Text.", [
    ["x ", ["bold"]],
    ["a`*b", ["bold", "code"]],
  ]);
  const doc = writable.editor.state.doc;
  const limited = roundTripError(() =>
    verifyComposition(
      writable.baseline,
      doc,
      composeMarkdown(writable.baseline, doc, createSpellingBudget(0)),
    ),
  );
  assert.equal(isExceptionFailure(writable.baseline, doc, limited), false);
  writable.editor.destroy();
  // 没有写法能读回的例外写法按例外接受。An exception construct that no spelling reads back is accepted.
  const unwritable = openSegments("Text.\n", "Text.", [
    ["run ", ["link"]],
    ["a`b", ["link", "code"]],
    [" now", ["link"]],
  ]);
  const failure = roundTripError(() =>
    writeMarkdown(unwritable.baseline, unwritable.editor.state.doc),
  );
  assert.equal(isExceptionFailure(unwritable.baseline, unwritable.editor.state.doc, failure), true);
  unwritable.editor.destroy();
});

test("inline code marked cannot read back fails to save without writing anything", () => {
  // 设计规定的两种例外，见 test-random-edits.ts 的 exceptionTexts。The two exceptions the design allows; see
  // exceptionTexts in test-random-edits.ts.
  const unwritable: [string, string, Segment[]][] = [
    // (a) 表格单元格中含有 `\|` 的行内代码。Inline code holding `\|` in a table cell.
    ["| a |\n| --- |\n| c |\n", "c", [["x\\|y", ["code"]]]],
    // (b) 链接文字中间含有反引号的行内代码。Inline code holding a backtick in the middle of link text.
    [
      "Text.\n",
      "Text.",
      [
        ["run ", ["link"]],
        ["a`b", ["link", "code"]],
        [" now", ["link"]],
      ],
    ],
    // (b) bold 与斜体中含有反引号、`*` 与 `_` 的行内代码。Inline code holding a backtick, `*` and `_` inside bold and
    // italic.
    [
      "Text.\n",
      "Text.",
      [
        ["x ", ["bold", "italic"]],
        ["a`*_b", ["bold", "italic", "code"]],
      ],
    ],
    // (b) 删除线中含有反引号、`~` 与 `~~` 的行内代码。Inline code holding a backtick, `~` and `~~` inside
    // strikethrough.
    [
      "Text.\n",
      "Text.",
      [
        ["x ", ["strike"]],
        ["a`~b~~", ["strike", "code"]],
      ],
    ],
  ];
  for (const [source, target, segments] of unwritable) {
    assert.throws(
      () => saveSegments(source, target, segments),
      MarkdownRoundTripError,
      JSON.stringify(segments),
    );
  }
});

test("dense marks around literal *, _ and ~ that no spelling reads back fail to save without writing anything", () => {
  // 设计规定的第三种例外：一段斜体包住四段 bold 与三段删除线，周围是字面的 `*`、`_`、`~` 与标点，约 9 个交替的标记
  // 边界；没有任何写法能读回相同的内容，不限写法搜索的量也一样。
  // The design's third exception: one italic run around four bold runs and three strikethrough runs, with literal
  // `*`, `_`, `~` and punctuation around about 9 alternating mark boundaries; no spelling reads the same content back,
  // also without a limit on the spelling search.
  const segments: Segment[] = [
    ["*,a,b", []],
    ["!aa ", ["bold", "italic"]],
    ["~", ["italic", "strike"]],
    ["!", ["bold", "italic"]],
    ["aa, ,.*_", ["italic", "strike"]],
    ["ba*.,", ["bold", "italic"]],
    ["_*", ["strike"]],
    ["!b_ ", ["bold", "italic", "strike"]],
    ["* !", ["italic"]],
  ];
  const { editor, baseline } = openSegments("Text.\n", "Text.", segments);
  const doc = editor.state.doc;
  // 内容校验在这个 paragraph 中失败。Content verification fails in this paragraph.
  const inParagraph = (error: unknown) =>
    error instanceof MarkdownRoundTripError &&
    error.failure.kind === "content" &&
    error.failure.path[0] === 0;
  assert.throws(() => writeMarkdown(baseline, doc), inParagraph);
  assert.throws(
    () =>
      verifyComposition(
        baseline,
        doc,
        composeMarkdown(baseline, doc, createSpellingBudget(Number.POSITIVE_INFINITY)),
      ),
    inParagraph,
  );
  editor.destroy();
});

test("inline code holding a backtick saves when another delimiter or fence can express it", () => {
  const writable: [Segment[], string][] = [
    [
      [
        ["x ", ["bold"]],
        ["a`*b", ["bold", "code"]],
      ],
      "__x ``a`*b``__\n",
    ],
    [
      [
        ["x ", ["italic"]],
        ["a`_b", ["italic", "code"]],
      ],
      "*x ``a`_b``*\n",
    ],
    [
      [
        ["x ", ["strike"]],
        ["a`~~b", ["strike", "code"]],
      ],
      "~x ``a`~~b``~\n",
    ],
    [
      [
        ["see ", ["link"]],
        ["`a", ["link", "code"]],
      ],
      "[see `` `a ``](/u)\n",
    ],
  ];
  for (const [segments, expected] of writable) {
    assert.equal(saveSegments("Text.\n", "Text.", segments), expected);
  }
});
