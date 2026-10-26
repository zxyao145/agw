import assert from "node:assert/strict";
import test from "node:test";
import { setupDomEnvironment } from "@agw/test-harness";
import type { JSONContent } from "@tiptap/core";

// jsdom 提供 window.DOMParser，测试与浏览器走同一条 HTML 解析路径。
// jsdom provides window.DOMParser, so the tests take the browser's HTML parsing path.
const { React } = await setupDomEnvironment();
const { normalizeMathDelimiters } = await import("@agw/chat-core");
const { MarkdownManager } = await import("@tiptap/markdown");
const { Table } = await import("@tiptap/extension-table");
const { Marked } = await import("marked");
const { renderToStaticMarkup } = await import("react-dom/server");
const { markdownExtensions } = await import("./markdown-extensions.ts");
const { normalizeMarkdownDocument } = await import("./normalize-document.ts");
const { parseMarkdown } = await import("./parse-markdown.ts");
const { TiptapMarkdown } = await import("./tiptap-markdown.tsx");

const FENCE = "```";

function parse(markdown: string): JSONContent {
  return normalizeMarkdownDocument(parseMarkdown(markdown));
}

function blocks(markdown: string): JSONContent[] {
  return parse(markdown).content ?? [];
}

function findAll(node: JSONContent, type: string): JSONContent[] {
  const own = node.type === type ? [node] : [];
  return [...own, ...(node.content ?? []).flatMap((child) => findAll(child, type))];
}

function textOf(node: JSONContent): string {
  return node.type === "text" ? (node.text ?? "") : (node.content ?? []).map(textOf).join("");
}

function types(nodes: JSONContent[] | undefined): (string | undefined)[] {
  return (nodes ?? []).map((node) => node.type);
}

test("raw HTML blocks and tags parse as literal text", () => {
  for (const html of [
    "<script>alert(1)</script>",
    "x <b>bold</b> y",
    '<img src="x" onerror="alert(1)">',
  ]) {
    const [paragraph, ...rest] = blocks(html);
    assert.deepEqual(rest, []);
    assert.deepEqual(paragraph, { type: "paragraph", content: [{ type: "text", text: html }] });
  }
  assert.deepEqual(blocks("<div>\n  a\n</div>"), [
    { type: "paragraph", content: [{ type: "text", text: "<div>\na\n</div>" }] },
  ]);
});

test("entities inside HTML stay verbatim while text entities decode as in CommonMark", () => {
  assert.equal(textOf(parse("<div>x &amp; y</div>")), "<div>x &amp; y</div>");
  assert.equal(textOf(parse('see <a title="&amp;">x</a>')), 'see <a title="&amp;">x</a>');
  assert.equal(textOf(parse("a &amp; b")), "a & b");
  assert.equal(textOf(parse("hello <b>&amp;</b>")), "hello <b>&</b>");
  assert.equal(textOf(parse("&copy; &amp;lt; &copy")), "© &lt; &copy");
});

test("an HTML block after a paragraph cut short by block math keeps the following paragraph", () => {
  assert.deepEqual(
    blocks("first text\n$$x$$\n<div>a</div>\n\nmore text").map((node) => [node.type, textOf(node)]),
    [
      ["paragraph", "first text"],
      ["blockMath", ""],
      ["paragraph", "<div>a</div>"],
      ["paragraph", "more text"],
    ],
  );
});

test("blank lines and document edges leave no empty paragraphs", () => {
  assert.deepEqual(types(blocks("a\n\n\n\nb")), ["paragraph", "paragraph"]);
  assert.deepEqual(types(blocks("\n\n\na\n\n\n")), ["paragraph"]);
  assert.deepEqual(types(blocks(normalizeMathDelimiters(String.raw`\[x\]`))), ["blockMath"]);
});

test("a paragraph holding only a non-breaking space is kept", () => {
  assert.deepEqual(blocks("&nbsp;"), [
    { type: "paragraph", content: [{ type: "text", text: " " }] },
  ]);
});

test("fenced code blocks keep their language, fence style and content", () => {
  const cases: [string, string | null, string][] = [
    ["  " + FENCE + "ts\nconst a = 1;\n  " + FENCE, "ts", "const a = 1;"],
    ["````md\n" + FENCE + "\ninner\n" + FENCE + "\n````", "md", FENCE + "\ninner\n" + FENCE],
    ["~~~py\nprint()\n~~~", "py", "print()"],
    [FENCE + "ts title=x\ncode\n" + FENCE, "ts", "code"],
    [FENCE + "js\nconst open", "js", "const open"],
  ];
  for (const [markdown, language, text] of cases) {
    const [codeBlock] = blocks(markdown);
    assert.equal(codeBlock.type, "codeBlock", markdown);
    assert.deepEqual(codeBlock.attrs, { language, fenced: true }, markdown);
    assert.equal(textOf(codeBlock), text, markdown);
  }
});

test("indented code is not fenced and fenced indentation survives normalization", () => {
  const [, indented] = blocks("text\n\n    const answer = 42;");
  assert.deepEqual(indented.attrs, { language: null, fenced: false });

  const [python] = blocks(FENCE + "py\nif ready:\n    run()\n" + FENCE);
  assert.equal(textOf(python), "if ready:\n    run()");
});

test("plus signs stay plain text", () => {
  for (const markdown of ["C++ and C++", "++x++"]) {
    assert.deepEqual(blocks(markdown), [
      { type: "paragraph", content: [{ type: "text", text: markdown }] },
    ]);
  }
});

test("lists record whether they are tight", () => {
  assert.equal(blocks("- a\n- b")[0].attrs?.tight, true);
  assert.equal(blocks("- a\n\n- b")[0].attrs?.tight, false);
  assert.deepEqual(blocks("3. a\n4. b")[0].attrs, { start: 3, tight: true });
});

test("a loose task list stays one list with a paragraph in every item", () => {
  const [list, ...rest] = blocks("- [ ] a\n\n- [ ] b");
  assert.deepEqual(rest, []);
  assert.equal(list.type, "bulletList");
  assert.equal(list.attrs?.tight, false);
  assert.deepEqual(
    list.content?.map((item) => [item.attrs?.checked, types(item.content), textOf(item)]),
    [
      [false, ["paragraph"], "a"],
      [false, ["paragraph"], "b"],
    ],
  );
});

test("tight task items keep their text in a paragraph and their state in checked", () => {
  const [list] = blocks("- [x] done\n- [ ] todo");
  assert.deepEqual(list.content, [
    {
      type: "listItem",
      attrs: { checked: true },
      content: [{ type: "paragraph", content: [{ type: "text", text: "done" }] }],
    },
    {
      type: "listItem",
      attrs: { checked: false },
      content: [{ type: "paragraph", content: [{ type: "text", text: "todo" }] }],
    },
  ]);
});

test("nested and mixed task lists keep each item's state", () => {
  const [outer] = blocks("- [ ] parent\n  - [x] child");
  const [parent] = outer.content ?? [];
  assert.equal(parent.attrs?.checked, false);
  assert.deepEqual(types(parent.content), ["paragraph", "bulletList"]);
  assert.equal(parent.content?.[1].content?.[0].attrs?.checked, true);

  const mixed = blocks("- plain\n- [x] task");
  assert.equal(mixed.length, 1);
  assert.deepEqual(
    mixed[0].content?.map((item) => item.attrs?.checked),
    [null, true],
  );
});

test("a task item continuation line joins the first paragraph", () => {
  const [list] = blocks("- [x] done\n  more");
  assert.deepEqual(list.content?.[0].content, [
    { type: "paragraph", content: [{ type: "text", text: "done\nmore" }] },
  ]);
});

test("tables keep the header row and column alignment", () => {
  const [table] = blocks("| a | b |\n|:-|-:|\n| 1 | 2 |");
  assert.deepEqual(
    table.content?.map((row) => row.content?.map((cell) => [cell.type, cell.attrs?.align])),
    [
      [
        ["tableHeader", "left"],
        ["tableHeader", "right"],
      ],
      [
        ["tableCell", "left"],
        ["tableCell", "right"],
      ],
    ],
  );
});

test("images stay inside their paragraph", () => {
  assert.deepEqual(types(blocks("see ![x](y.png) here")[0].content), ["text", "image", "text"]);
  const [image] = blocks("![x](x)");
  assert.equal(image.type, "paragraph");
  assert.deepEqual(types(image.content), ["image"]);
  assert.equal(image.content?.[0].attrs?.src, "x");
  for (const markdown of ["- ![x](x)\n\n", "- ![x](x)\n\n- y"]) {
    const [item] = blocks(markdown)[0].content ?? [];
    assert.deepEqual(types(item.content), ["paragraph"], markdown);
    assert.deepEqual(types(item.content?.[0].content), ["image"], markdown);
  }
});

function tableManagers() {
  const original = markdownExtensions.map((extension) =>
    extension.name === "table" ? Table : extension,
  );
  return [original, markdownExtensions].map(
    (extensions) => (markdown: string) =>
      new MarkdownManager({ marked: new Marked() as never, extensions }).parse(markdown),
  );
}

test("the table tokenizer replacement parses the same as the original", () => {
  const [parseOriginal, parseReplaced] = tableManagers();
  for (const markdown of [
    "| a | b |\n| --- | --- |\n| 1 | 2 |",
    "plain text\nmore text",
    "| only one line |",
    "| a | b |\n --- --- \n| 1 | 2 |",
    "| a | b |\n| ::: | ::: |\n| 1 | 2 |",
    "| a | b |\n\n| 1 | 2 |",
    "| a | b |\n| :-- | --: |\n| `x|y` | a \\| b |",
    "| a | b |\n| --- | --- |\n| 1 | `x|y` |\ntrailing text row",
  ]) {
    assert.deepEqual(parseReplaced(markdown), parseOriginal(markdown), markdown);
  }
});

test("content right after a table with a code pipe is kept", () => {
  const table = "| a | b |\n| --- | --- |\n| `x|y` | z |\n";
  for (const [after, type] of [
    ["# Heading", "heading"],
    ["> quote", "blockquote"],
    ["- item", "bulletList"],
  ]) {
    assert.deepEqual(types(blocks(table + after)), ["table", type], after);
  }
});

test("headings and tables alternating without blank lines parse as separate tables", () => {
  const section = (index: number) =>
    `# Section ${index}\n| a | b |\n| --- | --- |\n| \`x|y\` | ${index} |`;
  const markdown = [0, 1, 2].map(section).join("\n");
  assert.deepEqual(types(blocks(markdown)), [
    "heading",
    "table",
    "heading",
    "table",
    "heading",
    "table",
  ]);
});

test("line breaks follow CommonMark", () => {
  assert.deepEqual(blocks("a\nb")[0].content, [{ type: "text", text: "a\nb" }]);
  assert.deepEqual(types(blocks("a  \nb")[0].content), ["text", "hardBreak", "text"]);
  assert.deepEqual(blocks("a\n   b")[0].content, [{ type: "text", text: "a\nb" }]);
});

test("footnotes are numbered by first reference and listed at the end", () => {
  const document = parse("A[^two] B[^one] C[^two]\n\n[^one]: One.\n[^two]: Two.\n[^unused]: No.");
  assert.deepEqual(
    findAll(document, "footnoteReference").map((node) => node.attrs),
    [
      { label: "two", number: "1", index: 0 },
      { label: "one", number: "2", index: 0 },
      { label: "two", number: "1", index: 1 },
    ],
  );
  const footnotes = document.content?.at(-1);
  assert.equal(footnotes?.type, "footnotes");
  assert.deepEqual(
    footnotes?.content?.map((item) => [item.attrs, textOf(item)]),
    [
      [{ label: "two", number: "1", refCount: 2 }, "Two."],
      [{ label: "one", number: "2", refCount: 1 }, "One."],
    ],
  );
});

test("a reference without a definition stays text and no footnotes section appears", () => {
  const document = parse("See [^nope].");
  assert.equal(textOf(document), "See [^nope].");
  assert.deepEqual(types(document.content), ["paragraph"]);
});

test("footnote references resolve inside table cells and task items", () => {
  const inTable = parse("| a |\n| --- |\n| x[^n] |\n\n[^n]: Note.");
  assert.equal(findAll(findAll(inTable, "table")[0], "footnoteReference").length, 1);

  const inTask = parse("- [ ] task[^a]\n  more[^b]\n\n[^a]: A.\n[^b]: B.");
  assert.deepEqual(
    findAll(inTask, "footnoteReference").map((node) => node.attrs?.label),
    ["a", "b"],
  );

  const mixed = parse("- plain\n- [ ] task\n  see [^n]\n\n[^n]: Footnote text.");
  assert.equal(findAll(findAll(mixed, "bulletList")[0], "footnoteReference").length, 1);
  assert.equal(mixed.content?.at(-1)?.type, "footnotes");
});

test("documents that start with a task list or a math block still resolve references", () => {
  for (const start of ["- [ ] a\n- [x] b", "$$\nx\n$$"]) {
    const document = parse(`${start}\n\nText[^1]\n\n[^1]: Note.`);
    assert.equal(findAll(document, "footnoteReference").length, 1, start);
  }
});

test("every parse numbers footnotes from one", () => {
  for (let run = 0; run < 2; run += 1) {
    const [reference] = findAll(parse("x[^b]\n\n[^b]: B."), "footnoteReference");
    assert.equal(reference.attrs?.number, "1");
  }
});

test("math parses inline and as blocks", () => {
  assert.deepEqual(types(blocks("$x$")[0].content), ["inlineMath"]);
  assert.deepEqual(types(blocks(normalizeMathDelimiters(String.raw`\(x\)`))[0].content), [
    "inlineMath",
  ]);
  assert.deepEqual(blocks("$$\nx\n$$"), [{ type: "blockMath", attrs: { latex: "x" } }]);
  const [paragraph, ...rest] = blocks("where $$x$$ is");
  assert.deepEqual(rest, []);
  assert.deepEqual(types(paragraph.content), ["text", "inlineMath", "text"]);
  assert.deepEqual(blocks("costs $$$"), [
    { type: "paragraph", content: [{ type: "text", text: "costs $$$" }] },
  ]);
});

const STREAMING_SAMPLE = [
  "# Title[^n]",
  "",
  "Text with **bold**, *em*, ~~del~~, `code`, www.example.com, [link](https://x.y) and ![i](i.png).",
  "C++ and C++ cost $5. Math $x^2$ and \\(y\\) and $$z$$ inline. a &amp; b <b>raw</b>",
  "",
  "\\[",
  "E = mc^2",
  "\\]",
  "",
  "| a | b |",
  "| :- | -: |",
  "| `x|y` | 2[^n] |",
  "# After table",
  "- plain",
  "- [ ] task",
  "  see [^n]",
  "",
  "- [x] loose",
  "",
  "1. one",
  "2. two",
  "",
  "> quote",
  "",
  FENCE + "py",
  "if ready:",
  "    run()",
  FENCE,
  "",
  "    indented code",
  "",
  "<div>",
  "  block &amp;",
  "</div>",
  "",
  "---",
  "",
  "[^n]: Footnote text.",
].join("\n");

test("every streaming prefix of a full sample parses and renders", () => {
  for (let length = 0; length <= STREAMING_SAMPLE.length; length += 1) {
    const markdown = STREAMING_SAMPLE.slice(0, length);
    renderToStaticMarkup(React.createElement(TiptapMarkdown, { markdown }));
  }
});
