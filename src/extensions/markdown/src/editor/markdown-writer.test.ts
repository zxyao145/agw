import assert from "node:assert/strict";
import test from "node:test";

import type { Editor, JSONContent } from "@tiptap/core";
import { MarkdownManager } from "@tiptap/markdown";
import type { Node as ProseMirrorNode, Schema } from "@tiptap/pm/model";
import fc from "fast-check";
import { Marked, type marked as globalMarked } from "marked";

import { openMarkdown, readTestInputs, type LoadedEditor } from "./test-editor";

const { writeMarkdown, composeMarkdown, verifyComposition, MarkdownRoundTripError } =
  await import("./markdown-writer");
const { createSpellingBudget } = await import("../markdown/markdown-inline");
const { editorExtensions } = await import("./editor-extensions");
const { loadMarkdown } = await import("./markdown-loader");
const { comparableDocument } = await import("./document-equivalence");
const { FrontMatterError } = await import("../markdown/front-matter");

const FENCE = "```";

function save({ editor, baseline }: LoadedEditor): string {
  const result = writeMarkdown(baseline, editor.state.doc);
  assert.ok(result, "the document should have changed");
  return result.text;
}

/** 当前文档的某个节点：按类型与文字查找，返回位置。A node of the current document found by type and text. */
function find(
  editor: Editor,
  predicate: (node: ProseMirrorNode) => boolean,
): { node: ProseMirrorNode; pos: number } {
  let found: { node: ProseMirrorNode; pos: number } | null = null;
  editor.state.doc.descendants((node, pos) => {
    if (!found && predicate(node)) found = { node, pos };
    return !found;
  });
  assert.ok(found, "node not found");
  return found;
}

function replaceText(editor: Editor, pos: number, node: ProseMirrorNode, text: string): void {
  editor.view.dispatch(editor.state.tr.insertText(text, pos + 1, pos + 1 + node.content.size));
}

function reparse(text: string, editor: Editor): ProseMirrorNode {
  return loadMarkdown(text, editor.schema).doc;
}

function differingRange(before: string, after: string): { from: number; to: number } {
  let prefix = 0;
  while (prefix < before.length && prefix < after.length && before[prefix] === after[prefix])
    prefix++;
  let suffix = 0;
  while (
    suffix < before.length - prefix &&
    suffix < after.length - prefix &&
    before[before.length - 1 - suffix] === after[after.length - 1 - suffix]
  ) {
    suffix++;
  }
  return { from: prefix, to: before.length - suffix };
}

test("saving an unmodified document writes nothing", () => {
  for (const { name, source } of readTestInputs()) {
    const { editor, baseline } = openMarkdown(source);
    assert.equal(writeMarkdown(baseline, editor.state.doc), null, name);
  }
});

test("changing one paragraph changes only that paragraph's block or top-level list item", () => {
  for (const { name, source } of readTestInputs()) {
    const paragraphs = openMarkdown(source);
    const positions: number[] = [];
    paragraphs.editor.state.doc.descendants((node, pos) => {
      if (node.type.name === "paragraph" && node.content.size) positions.push(pos);
    });
    for (const position of positions) {
      const loaded = openMarkdown(source);
      const { editor, baseline } = loaded;
      const node = editor.state.doc.nodeAt(position)!;
      replaceText(editor, position, node, "Changed words");
      const written = save(loaded);
      // 被修改的顶层块（或顶层列表的列表项）以及它两边的间隔之外，其余字节不变。
      // Outside the modified top-level block (or top-level list item) and its gaps every byte is unchanged.
      const $pos = editor.state.doc.resolve(position);
      const blockIndex = $pos.index(0);
      const block = baseline.blocks[blockIndex];
      let from = blockIndex > 0 ? baseline.blocks[blockIndex - 1].range.to : 0;
      let to =
        blockIndex + 1 < baseline.blocks.length
          ? baseline.blocks[blockIndex + 1].range.from
          : source.length;
      if (block.items && $pos.depth >= 2) {
        const itemIndex = $pos.index(1);
        from = itemIndex > 0 ? block.items[itemIndex - 1].to : block.range.from;
        to = itemIndex + 1 < block.items.length ? block.items[itemIndex + 1].from : block.range.to;
      }
      const changed = differingRange(source, written);
      assert.ok(
        changed.from >= from && changed.to <= to,
        `${name} at ${position}: changed ${JSON.stringify(source.slice(changed.from, changed.to))}`,
      );
      assert.ok(written.includes("Changed words"), `${name} at ${position}`);
    }
  }
});

test("checking a task or editing a list item rewrites only that item, keeping the others' markers and numbers", () => {
  const source = "* one\n* two\n* three\n\n3. x\n4. y\n5. z\n\n- [ ] first\n- [ ] second\n";
  const tasks = openMarkdown(source);
  const second = find(
    tasks.editor,
    (node) => node.type.name === "listItem" && node.textContent === "second",
  );
  tasks.editor.view.dispatch(
    tasks.editor.state.tr.setNodeMarkup(second.pos, undefined, {
      ...second.node.attrs,
      checked: true,
    }),
  );
  assert.equal(save(tasks), source.replace("- [ ] second", "- [x] second"));

  const items = openMarkdown(source);
  const two = find(
    items.editor,
    (node) => node.type.name === "paragraph" && node.textContent === "two",
  );
  replaceText(items.editor, two.pos, two.node, "TWO");
  const y = find(
    items.editor,
    (node) => node.type.name === "paragraph" && node.textContent === "y",
  );
  replaceText(items.editor, y.pos, y.node, "Y");
  assert.equal(save(items), source.replace("* two", "* TWO").replace("4. y", "4. Y"));
});

test("editing a definition keeps the duplicate definition, the code fence info and the referencing paragraph", () => {
  const source = [
    "See [the site][site] and [site].",
    "",
    "[site]: https://old.example.com",
    "[site]: https://duplicate.example.com",
    "",
    `${FENCE}ts title=x`,
    "const a = 1;",
    FENCE,
    "",
  ].join("\n");
  const loaded = openMarkdown(source);
  const definition = find(
    loaded.editor,
    (node) => node.textContent === "[site]: https://old.example.com",
  );
  replaceText(loaded.editor, definition.pos, definition.node, "[site]: https://new.example.com");
  const written = save(loaded);
  assert.equal(written, source.replace("https://old.example.com", "https://new.example.com"));
  const hrefs: unknown[] = [];
  reparse(written, loaded.editor).descendants((node) => {
    node.marks.forEach((mark) => mark.type.name === "link" && hrefs.push(mark.attrs.href));
  });
  assert.deepEqual(hrefs, ["https://new.example.com", "https://new.example.com"]);
});

test("deleting a definition saves the referencing paragraph, which parses back as text", () => {
  const source = "See [the site][site].\n\n[site]: https://example.com\n";
  const loaded = openMarkdown(source);
  const definition = find(loaded.editor, (node) => node.type.name === "rawBlock");
  loaded.editor.view.dispatch(
    loaded.editor.state.tr.delete(definition.pos, definition.pos + definition.node.nodeSize),
  );
  const written = save(loaded);
  assert.equal(written, "See [the site][site].\n");
  const paragraph = reparse(written, loaded.editor).child(0);
  assert.equal(paragraph.textContent, "See [the site][site].");
  assert.equal(paragraph.firstChild!.marks.length, 0);
});

test("a reference image follows its definition and keeps its reference spelling", () => {
  for (const image of ["![x][logo]", "![logo][]", "![logo]"]) {
    const source = `Logo ${image} here.\n\n[logo]: /old.png\n`;
    const definition = openMarkdown(source);
    const block = find(definition.editor, (node) => node.type.name === "rawBlock");
    replaceText(definition.editor, block.pos, block.node, '[logo]: /new.png "New"');
    const written = save(definition);
    assert.equal(written, source.replace("/old.png", '/new.png "New"'), image);
    const reparsed = reparse(written, definition.editor).child(0).child(1);
    assert.deepEqual([reparsed.attrs.src, reparsed.attrs.title], ["/new.png", "New"], image);

    const paragraph = openMarkdown(source);
    paragraph.editor.view.dispatch(paragraph.editor.state.tr.insertText("New ", 1));
    assert.equal(save(paragraph), `New ${source}`, image);
  }
  const deleted = openMarkdown("Logo ![x][logo] here.\n\n[logo]: /old.png\n");
  const block = find(deleted.editor, (node) => node.type.name === "rawBlock");
  deleted.editor.view.dispatch(
    deleted.editor.state.tr.delete(block.pos, block.pos + block.node.nodeSize),
  );
  const written = save(deleted);
  assert.equal(written, "Logo ![x][logo] here.\n");
  assert.equal(reparse(written, deleted.editor).child(0).childCount, 1);
});

test("a reference image whose definition is deleted reads its alt as inline Markdown, as marked does", () => {
  for (const alt of ["*x*", "a\\*b", "a &amp; b", "`c`"]) {
    const source = `Logo ![${alt}][logo] here.\n\n[logo]: /old.png\n`;
    const deleteDefinition = (loaded: LoadedEditor) => {
      const block = find(loaded.editor, (node) => node.type.name === "rawBlock");
      loaded.editor.view.dispatch(
        loaded.editor.state.tr.delete(block.pos, block.pos + block.node.nodeSize),
      );
    };
    // 只删除定义：paragraph 保留原文。Only the definition deleted: the paragraph keeps its source.
    const untouched = openMarkdown(source);
    deleteDefinition(untouched);
    assert.equal(save(untouched), `Logo ![${alt}][logo] here.\n`, alt);

    // 同时修改 paragraph：写出的文字读回 marked 读原文得到的内容。The paragraph edited too: the written text reads back
    // as marked reads the source.
    const edited = openMarkdown(source);
    deleteDefinition(edited);
    edited.editor.view.dispatch(edited.editor.state.tr.insertText("New ", 1));
    const written = save(edited);
    const expected = reparse(`New Logo ![${alt}][logo] here.\n`, edited.editor).child(0);
    assert.ok(reparse(written, edited.editor).child(0).eq(expected), `${alt}: ${written}`);
  }
});

test("image destinations and titles are written by mdast-util-to-markdown and alt text by marked's rule", () => {
  const image = (attrs: Record<string, unknown>): JSONContent => ({ type: "image", attrs });
  assert.equal(
    writeInline([image({ src: "a b.png", alt: "x]y", title: 'q"t' })]),
    '![x\\]y](<a b.png> "q\\"t")',
  );
  assert.equal(
    writeInline([image({ src: "/a(b).png", alt: "a*b", title: null })]),
    "![a*b](/a\\(b\\).png)",
  );
});

test("code blocks switch to a fence when the indented form cannot hold their content or info", () => {
  const source = "Text.\n\n    indented\n\nMore.\n\n    ```\n    not a fence\n";
  const blank = openMarkdown(source);
  const first = find(blank.editor, (node) => node.type.name === "codeBlock");
  blank.editor.view.dispatch(blank.editor.state.tr.insertText("\n", first.pos + 1));
  assert.equal(save(blank), "Text.\n\n```\n\nindented\n```\n\nMore.\n\n    ```\n    not a fence\n");

  const language = openMarkdown(source);
  const code = find(language.editor, (node) => node.type.name === "codeBlock");
  language.editor.view.dispatch(
    language.editor.state.tr.setNodeMarkup(code.pos, undefined, {
      ...code.node.attrs,
      language: "js",
      info: "js",
    }),
  );
  const written = save(language);
  assert.equal(written, "Text.\n\n```js\nindented\n```\n\nMore.\n\n    ```\n    not a fence\n");
  assert.equal(reparse(written, language.editor).child(1).attrs.info, "js");

  const backticks = openMarkdown(source);
  const last = find(
    backticks.editor,
    (node) => node.type.name === "codeBlock" && node.textContent.startsWith("```"),
  );
  backticks.editor.view.dispatch(
    backticks.editor.state.tr.setNodeMarkup(last.pos, undefined, {
      ...last.node.attrs,
      language: "md",
      info: "md",
    }),
  );
  assert.equal(
    save(backticks),
    "Text.\n\n    indented\n\nMore.\n\n````md\n```\nnot a fence\n````\n",
  );
});

// 替换链接文字并保留链接标记，与编辑链接文字相同。Replaces the link text keeping the link mark, as editing the link
// text does.
function replaceLinkText(loaded: LoadedEditor, text: string): void {
  const { editor } = loaded;
  const link = find(
    editor,
    (node) => node.isText && node.marks.some((mark) => mark.type.name === "link"),
  );
  editor.view.dispatch(
    editor.state.tr.replaceWith(
      link.pos,
      link.pos + link.node.nodeSize,
      editor.schema.text(text, link.node.marks),
    ),
  );
}

test("reference link text changes keep pointing at the same definition", () => {
  for (const [link, expected] of [
    ["[foo][]", "[bar][foo]"],
    ["[foo]", "[bar][foo]"],
  ]) {
    const source = `Go to ${link} now.\n\n[foo]: /target\n`;
    const loaded = openMarkdown(source);
    replaceLinkText(loaded, "bar");
    const written = save(loaded);
    assert.equal(written, `Go to ${expected} now.\n\n[foo]: /target\n`);
    const reparsed = reparse(written, loaded.editor).child(0);
    assert.equal(reparsed.child(1).marks[0].attrs.href, "/target");
  }
  const loaded = openMarkdown("Go to [foo][] now.\n\n[foo]: /target\n");
  replaceLinkText(loaded, "Foo");
  assert.equal(save(loaded), "Go to [Foo][] now.\n\n[foo]: /target\n");
});

test("boundary rules keep neighboring lists apart, fence indented code after a list and rewrite indented blocks", () => {
  const lists = openMarkdown("* a\n* b\n\nBetween.\n\n* c\n* d\n");
  const between = find(
    lists.editor,
    (node) => node.type.name === "paragraph" && node.textContent === "Between.",
  );
  lists.editor.view.dispatch(
    lists.editor.state.tr.delete(between.pos, between.pos + between.node.nodeSize),
  );
  const written = save(lists);
  assert.equal(written, "* a\n* b\n\n- c\n- d\n");
  const reparsed = reparse(written, lists.editor);
  assert.deepEqual(
    [reparsed.childCount, reparsed.child(0).childCount, reparsed.child(1).childCount],
    [2, 2, 2],
  );

  const code = openMarkdown("- a\n- b\n\nEnd.\n");
  const end = find(
    code.editor,
    (node) => node.type.name === "paragraph" && node.textContent === "End.",
  );
  code.editor.view.dispatch(
    code.editor.state.tr.insert(
      end.pos,
      code.editor.schema.nodes.codeBlock.create({ fenced: false }, code.editor.schema.text("code")),
    ),
  );
  assert.equal(save(code), "- a\n- b\n\n```\ncode\n```\n\nEnd.\n");

  const indented = openMarkdown("- a\n\n# Title\n\n   spaced paragraph\n");
  const title = find(indented.editor, (node) => node.type.name === "heading");
  indented.editor.view.dispatch(
    indented.editor.state.tr.delete(title.pos, title.pos + title.node.nodeSize),
  );
  assert.equal(save(indented), "- a\n\nspaced paragraph\n");
});

/** 删除第一个满足条件的顶层块。Deletes the first top-level block that matches. */
function deleteTopLevel(editor: Editor, predicate: (node: ProseMirrorNode) => boolean): void {
  let target: { node: ProseMirrorNode; pos: number } | null = null;
  editor.state.doc.forEach((node, pos) => {
    if (!target && predicate(node)) target = { node, pos };
  });
  assert.ok(target, "top-level block not found");
  const { node, pos } = target as { node: ProseMirrorNode; pos: number };
  editor.view.dispatch(editor.state.tr.delete(pos, pos + node.nodeSize));
}

/** 同一个编辑器的连续保存：每次保存以上一次写入后的基线为准。Consecutive saves of one editor: each save uses the
 *  baseline after the previous write. */
function sessionSaves(source: string): { editor: Editor; save(): string } {
  const loaded = openMarkdown(source);
  let { baseline } = loaded;
  return {
    editor: loaded.editor,
    save() {
      const result = writeMarkdown(baseline, loaded.editor.state.doc);
      assert.ok(result, "the document should have changed");
      baseline = result.baseline;
      return result.text;
    },
  };
}

test("later saves in one session take a kept list's symbol from the text the earlier save wrote", () => {
  // 第一次保存时边界规则让第二个列表改用另一个符号，编辑器中它的 marker 或 delimiter 不变。
  // The first save's boundary rule writes the second list with the other symbol, while its marker or delimiter in
  // the editor stays the same.
  const isText = (text: string) => (node: ProseMirrorNode) => node.textContent === text;
  for (const [source, written] of [
    ["- a\n\npara\n\n- b1\n- b2\n", ["- a\n\n* b1\n* b2\n", "* b1\n* b2\n", "* b1\n* b2X\n"]],
    [
      "1. a\n\npara\n\n1. b1\n2. b2\n",
      ["1. a\n\n1) b1\n2) b2\n", "1) b1\n2) b2\n", "1) b1\n2) b2X\n"],
    ],
  ] as const) {
    const session = sessionSaves(source);
    deleteTopLevel(session.editor, isText("para"));
    assert.equal(session.save(), written[0], source);
    deleteTopLevel(session.editor, isText("a"));
    assert.equal(session.save(), written[1], source);
    const b2 = find(session.editor, (node) => node.type.name === "paragraph" && isText("b2")(node));
    session.editor.view.dispatch(
      session.editor.state.tr.insertText("X", b2.pos + 1 + b2.node.content.size),
    );
    assert.equal(session.save(), written[2], source);
  }
  // 改过符号的列表成为另一个同符号列表的邻居：再次按写入的符号判断，改用另一个符号。The renamed list becomes the
  // neighbor of another list with the same written symbol: the rule again uses the written symbol and switches.
  const session = sessionSaves("* x\n\n- a\n\npara\n\n- b\n");
  deleteTopLevel(session.editor, isText("para"));
  assert.equal(session.save(), "* x\n\n- a\n\n* b\n");
  deleteTopLevel(session.editor, isText("a"));
  assert.equal(session.save(), "* x\n\n- b\n");
});

/** 在文字为 `text` 的 paragraph 末尾输入 X。Types X at the end of the paragraph reading `text`. */
function appendX(editor: Editor, text: string): void {
  const target = find(
    editor,
    (node) => node.type.name === "paragraph" && node.textContent === text,
  );
  editor.view.dispatch(editor.state.tr.insertText("X", target.pos + 1 + target.node.content.size));
}

test("modified items of an indented list keep the indentation the list's kept items have in the source", () => {
  for (const source of [
    "  * a\n  * b\n  * c\n",
    "  - a\n  - b\n  - c\n",
    "   1. a\n   2. b\n   3. c\n",
  ]) {
    for (const item of ["a", "b", "c"]) {
      const loaded = openMarkdown(source);
      appendX(loaded.editor, item);
      assert.equal(save(loaded), source.replace(` ${item}\n`, ` ${item}X\n`), `${source} ${item}`);
    }
  }
  // 新加的列表项。An added item.
  const added = openMarkdown("  * a\n  * b\n  * c\n");
  const { schema } = added.editor;
  const b = find(added.editor, (node) => node.type.name === "listItem" && node.textContent === "b");
  added.editor.view.dispatch(
    added.editor.state.tr.insert(
      b.pos + b.node.nodeSize,
      schema.nodes.listItem.create(null, schema.nodes.paragraph.create(null, schema.text("new"))),
    ),
  );
  assert.equal(save(added), "  * a\n  * b\n  * new\n  * c\n");
  // 修改的列表项含有嵌套列表：每一行都加上缩进。A modified item holding a nested list: every line gets the
  // indentation.
  const nested = openMarkdown("  * a\n    * n\n  * b\n");
  appendX(nested.editor, "a");
  assert.equal(save(nested), "  * aX\n    * n\n  * b\n");
  // 同一个编辑器中的两次保存。Two saves in one editor.
  const session = sessionSaves("  * a\n  * b\n  * c\n");
  appendX(session.editor, "a");
  assert.equal(session.save(), "  * aX\n  * b\n  * c\n");
  appendX(session.editor, "c");
  assert.equal(session.save(), "  * aX\n  * b\n  * cX\n");
});

test("an indented list compared item by item after another list is written without indentation", () => {
  // 前面的 paragraph 换成列表：有缩进的列表会被并入它的列表项，边界规则 3 让它整体按修改块输出。The paragraph before
  // is replaced by a list: the indented list would join its item, so boundary rule 3 writes it whole as a modified
  // block.
  const loaded = openMarkdown("Para.\n\n  * a\n  * b\n");
  const { editor } = loaded;
  const { schema } = editor;
  const list = schema.nodes.bulletList.create(
    { marker: "-" },
    schema.nodes.listItem.create(null, schema.nodes.paragraph.create(null, schema.text("Para."))),
  );
  editor.view.dispatch(editor.state.tr.replaceWith(0, editor.state.doc.child(0).nodeSize, list));
  appendX(editor, "a");
  assert.equal(save(loaded), "- Para.\n\n* aX\n* b\n");
});

test("list items keep the list's start number and looseness when items are deleted", () => {
  const deleteItem = (source: string, text: string) => {
    const loaded = openMarkdown(source);
    const item = find(
      loaded.editor,
      (node) => node.type.name === "listItem" && node.textContent === text,
    );
    loaded.editor.view.dispatch(
      loaded.editor.state.tr.delete(item.pos, item.pos + item.node.nodeSize),
    );
    return save(loaded);
  };
  // marked 只用第一项的编号作为 start。marked takes start from the first item only.
  assert.equal(deleteItem("1. a\n2. b\n3. c\n", "a"), "1. b\n3. c\n");
  // 删除后只有一项的列表总是紧凑的。A list left with one item is always tight.
  assert.equal(deleteItem("- a\n\n- b\n", "b"), "- a\n");
  // 非紧凑列表保留一个空行。A loose list keeps a blank line.
  assert.equal(deleteItem("- a\n- b\n\n- c\n", "c"), "- a\n\n- b\n");
});

test("the text before the first block and after the last block stays while those blocks stay in place", () => {
  const loaded = openMarkdown("\n\nFirst.\n\nLast.\n\n\n");
  const first = find(loaded.editor, (node) => node.textContent === "First.");
  replaceText(loaded.editor, first.pos, first.node, "Changed.");
  const last = find(loaded.editor, (node) => node.textContent === "Last.");
  replaceText(loaded.editor, last.pos, last.node, "Also changed.");
  assert.equal(save(loaded), "\n\nChanged.\n\nAlso changed.\n\n\n");

  const appended = openMarkdown("Only.\n\n\n");
  const { editor } = appended;
  editor.view.dispatch(
    editor.state.tr.insert(
      editor.state.doc.content.size,
      editor.schema.nodes.paragraph.create(null, editor.schema.text("New.")),
    ),
  );
  assert.equal(save(appended), "Only.\n\nNew.\n");
});

test("edited front matter is written between two --- lines", () => {
  const loaded = openMarkdown("---\ntitle: a\n---\n\nText.\n");
  const frontMatter = find(loaded.editor, (node) => node.type.name === "frontMatter");
  replaceText(loaded.editor, frontMatter.pos, frontMatter.node, "title: b\ntags: [x]");
  assert.equal(save(loaded), "---\ntitle: b\ntags: [x]\n---\n\nText.\n");
});

test("front matter edited into invalid YAML fails to save with the YAML message", () => {
  const loaded = openMarkdown("---\ntitle: a\n---\n\nText.\n");
  const frontMatter = find(loaded.editor, (node) => node.type.name === "frontMatter");
  replaceText(loaded.editor, frontMatter.pos, frontMatter.node, "title: [a");
  assert.throws(
    () => writeMarkdown(loaded.baseline, loaded.editor.state.doc),
    (error: unknown) =>
      error instanceof FrontMatterError &&
      error.message.startsWith("Front matter isn't valid YAML: Flow sequence in block collection"),
  );
});

test("a file holding only front matter gets a body typed into its empty paragraph", () => {
  for (const [source, expected] of [
    ["---\ntitle: a\n---\n", "---\ntitle: a\n---\n\nBody.\n"],
    ["---\ntitle: a\n---", "---\ntitle: a\n---\n\nBody."],
    ["---\ntitle: a\n---\n\n", "---\ntitle: a\n---\n\nBody.\n\n"],
  ]) {
    const loaded = openMarkdown(source);
    const paragraph = find(loaded.editor, (node) => node.type.name === "paragraph");
    replaceText(loaded.editor, paragraph.pos, paragraph.node, "Body.");
    assert.equal(save(loaded), expected, JSON.stringify(source));
  }
});

test("a document that cannot be written back throws MarkdownRoundTripError with the differing block path and types", () => {
  const loaded = openMarkdown("Text.\n\n<div>x</div>\n");
  // HTML 块的文字改为普通文字后，写出的文本读回为 paragraph。An HTML block whose text became plain words reads back
  // as a paragraph.
  const raw = find(loaded.editor, (node) => node.type.name === "rawBlock");
  replaceText(loaded.editor, raw.pos, raw.node, "plain words");
  let caught: unknown = null;
  try {
    writeMarkdown(loaded.baseline, loaded.editor.state.doc);
  } catch (error) {
    caught = error;
  }
  assert.ok(caught instanceof MarkdownRoundTripError, String(caught));
  assert.equal(
    caught.message,
    "The written Markdown parses to different content at block path [1]: expected rawBlock, found paragraph.",
  );
  assert.deepEqual(caught.failure, {
    kind: "content",
    path: [1],
    expected: "rawBlock",
    actual: "paragraph",
  });
});

test("a cleared raw block is left out of the written text, as an empty paragraph is", () => {
  const source = "Para.\n\n<div>\nhi\n</div>\n\nMore.\n";
  const session = sessionSaves(source);
  const raw = find(session.editor, (node) => node.type.name === "rawBlock");
  session.editor.view.dispatch(
    session.editor.state.tr.delete(raw.pos + 1, raw.pos + 1 + raw.node.content.size),
  );
  assert.equal(session.save(), "Para.\n\nMore.\n");
  // 下一次保存时空的原文块仍在编辑器中，它两边的块保持原样。On the next save the empty raw block is still in the
  // editor, and the blocks around it stay as written.
  const more = find(session.editor, (node) => node.textContent === "More.");
  session.editor.view.dispatch(
    session.editor.state.tr.insertText("!", more.pos + 1 + more.node.content.size),
  );
  assert.equal(session.save(), "Para.\n\nMore.!\n");
});

test("verification fails when a preserved block is not intact in the composed output", () => {
  const loaded = openMarkdown("First.\n\nSecond.\n");
  const second = find(loaded.editor, (node) => node.textContent === "Second.");
  replaceText(loaded.editor, second.pos, second.node, "Changed.");
  const composition = composeMarkdown(loaded.baseline, loaded.editor.state.doc);
  verifyComposition(loaded.baseline, loaded.editor.state.doc, composition);
  const moved = {
    ...composition,
    preserved: composition.preserved.map((span) => ({
      ...span,
      output: { from: span.output.from + 1, to: span.output.to + 1 },
    })),
  };
  assert.throws(
    () => verifyComposition(loaded.baseline, loaded.editor.state.doc, moved),
    (error: unknown) =>
      error instanceof MarkdownRoundTripError &&
      error.failure.kind === "preserved" &&
      error.failure.block === 0,
  );
});

type Segment =
  | { kind: "text"; text: string; marks: string[] }
  | { kind: "hardBreak" | "image" | "rawInline" | "footnoteReference" };

function inlineProperty(alphabet: string, markTypes: string[]) {
  const segment: fc.Arbitrary<Segment> = fc.oneof(
    {
      weight: 6,
      arbitrary: fc.record({
        kind: fc.constant("text" as const),
        text: fc.string({
          unit: fc.constantFrom(...alphabet.split("")),
          minLength: 1,
          maxLength: 10,
        }),
        marks: fc.subarray(markTypes),
      }),
    },
    {
      weight: 1,
      arbitrary: fc.record({
        kind: fc.constantFrom(
          "hardBreak" as const,
          "image" as const,
          "rawInline" as const,
          "footnoteReference" as const,
        ),
      }),
    },
  );
  return fc.property(fc.array(segment, { minLength: 1, maxLength: 8 }), (segments) => {
    const loaded = openMarkdown("Text.\n\n[^n]: Note.\n");
    const { editor } = loaded;
    const { schema } = editor;
    const nodes = segments.map((item) => {
      switch (item.kind) {
        case "hardBreak":
          return schema.nodes.hardBreak.create();
        case "image":
          return schema.nodes.image.create({ src: "a b.png", alt: "x]y", title: 'q"t' });
        case "rawInline":
          return schema.nodes.rawInline.create({ raw: "<span>" });
        case "footnoteReference":
          return schema.nodes.footnoteReference.create({ label: "n" });
        default:
          return schema.text(
            item.text,
            item.marks.map((type) =>
              type === "link"
                ? schema.marks.link.create({ href: "/a (b)" })
                : schema.marks[type].create(),
            ),
          );
      }
    });
    const paragraph = editor.state.doc.child(0);
    editor.view.dispatch(editor.state.tr.replaceWith(1, 1 + paragraph.content.size, nodes));
    writeMarkdown(loaded.baseline, editor.state.doc);
    editor.destroy();
  });
}

test("inline content with marks, inline nodes and Markdown punctuation saves with both verifications passing", () => {
  fc.assert(
    inlineProperty("ab .,!*_[]~\\#-+1=>|:()@w/&<", ["bold", "italic", "strike", "code", "link"]),
    { numRuns: 300, seed: 20261019 },
  );
  fc.assert(inlineProperty("ab .,!*_[]~\\#-+1=>|:()@w/&<`", ["bold", "italic", "strike", "link"]), {
    numRuns: 300,
    seed: 20261019,
  });
});

// 短文字与标点上任意组合的 bold、斜体与删除线。Any combination of bold, italic and strikethrough on short text and
// punctuation.
const denseSegments = fc.array(
  fc.record({
    text: fc.string({
      unit: fc.constantFrom(..."ab .,!*_~".split("")),
      minLength: 1,
      maxLength: 6,
    }),
    marks: fc.subarray(["bold", "italic", "strike"]),
  }),
  { minLength: 1, maxLength: 6 },
);

test("dense bold, italic and strikethrough next to punctuation save with both verifications passing", () => {
  for (const seed of [1, 2, 3, 4, 5]) {
    fc.assert(
      fc.property(denseSegments, (segments) => {
        const loaded = openMarkdown("Text.\n");
        const { editor } = loaded;
        const { schema } = editor;
        const nodes = segments.map((segment) =>
          schema.text(
            segment.text,
            segment.marks.map((type) => schema.marks[type].create()),
          ),
        );
        editor.view.dispatch(
          editor.state.tr.replaceWith(1, 1 + editor.state.doc.child(0).content.size, nodes),
        );
        writeMarkdown(loaded.baseline, editor.state.doc);
        editor.destroy();
      }),
      { numRuns: 300, seed },
    );
  }
});

/** 有 `count` 个 paragraph 的文档，每个的内容换成 `build` 生成的节点。A document of `count` paragraphs, each holding
 *  the nodes `build` makes. */
function paragraphsWith(count: number, build: (schema: Schema) => ProseMirrorNode[]): LoadedEditor {
  const source = `${Array.from({ length: count }, (_value, index) => `P${index}.`).join("\n\n")}\n`;
  const loaded = openMarkdown(source);
  const { editor } = loaded;
  const positions: number[] = [];
  editor.state.doc.forEach((_node, offset) => positions.push(offset));
  for (const pos of positions.reverse()) {
    const size = editor.state.doc.nodeAt(pos)!.content.size;
    editor.view.dispatch(
      editor.state.tr.replaceWith(pos + 1, pos + 1 + size, build(editor.schema)),
    );
  }
  return loaded;
}

/** `words` 个 bold 词，后面是链接文字中间含有反引号的行内代码（无法写出）。`words` bold words followed by inline code
 *  holding a backtick in the middle of link text (unwritable). */
function boldWordsAndUnwritableLink(words: number, word: (index: number) => string) {
  return (schema: Schema): ProseMirrorNode[] => {
    const link = schema.marks.link.create({ href: "/u" });
    const nodes: ProseMirrorNode[] = [];
    for (let index = 0; index < words; index++) {
      nodes.push(schema.text(word(index), [schema.marks.bold.create()]), schema.text(" "));
    }
    nodes.push(
      schema.text("run ", [link]),
      schema.text("a`b", [link, schema.marks.code.create()]),
      schema.text(" now", [link]),
    );
    return nodes;
  };
}

test("writing inline content without a save's spelling budget throws", () => {
  // 没有经过 createEditorRenderer 设置 spellingBudget 的 MarkdownManager。A MarkdownManager whose spellingBudget was
  // not set by createEditorRenderer.
  const manager = new MarkdownManager({
    marked: new Marked() as unknown as typeof globalMarked,
    extensions: editorExtensions,
  });
  assert.throws(
    () =>
      manager.renderNodes(
        { type: "paragraph", content: [{ type: "text", text: "a" }] },
        { type: "doc" },
      ),
    /createEditorRenderer/,
  );
});

const sentence = (index: number) => `sentence number ${index} with several plain words in it`;
const shortWord = (index: number) => `w${index}`;

test("a save stops searching for spellings after the first inline content that has none, and fails quickly", () => {
  const shapes: [number, number, (index: number) => string][] = [
    [8, 9, shortWord],
    [1, 60, sentence],
  ];
  for (const [count, words, word] of shapes) {
    const loaded = paragraphsWith(count, boldWordsAndUnwritableLink(words, word));
    const budget = createSpellingBudget();
    composeMarkdown(loaded.baseline, loaded.editor.state.doc, budget);
    assert.equal(budget.stopped, true);
    const start = performance.now();
    assert.throws(
      () => writeMarkdown(loaded.baseline, loaded.editor.state.doc),
      MarkdownRoundTripError,
    );
    const elapsed = performance.now() - start;
    assert.ok(elapsed < 1000, `${count} × ${words} words took ${elapsed.toFixed(0)} ms`);
  }
  // 后面需要其他写法的 paragraph 不再搜索，按默认写法输出。A later paragraph that needs another spelling is no longer
  // searched and keeps the default spelling.
  const loaded = openMarkdown("P0.\n\nP1.\n");
  const { editor } = loaded;
  const { schema } = editor;
  const second = editor.state.doc.child(0).nodeSize;
  editor.view.dispatch(
    editor.state.tr.replaceWith(second + 1, second + 1 + editor.state.doc.child(1).content.size, [
      schema.text("a.", [schema.marks.bold.create()]),
      schema.text("b", [schema.marks.strike.create()]),
    ]),
  );
  editor.view.dispatch(
    editor.state.tr.replaceWith(
      1,
      1 + editor.state.doc.child(0).content.size,
      boldWordsAndUnwritableLink(1, shortWord)(schema),
    ),
  );
  const composition = composeMarkdown(loaded.baseline, editor.state.doc, createSpellingBudget());
  assert.match(composition.text, /\n\n\*\*a\.\*\*~~b~~\n$/);
});

function boldPhrases(schema: Schema, count: number, phrase: (index: number) => string) {
  const nodes: ProseMirrorNode[] = [];
  for (let index = 0; index < count; index++) {
    nodes.push(schema.text(phrase(index), [schema.marks.bold.create()]), schema.text(" "));
  }
  return nodes;
}

/** `count` 个 bold 短语，后面是 `tails` 处 bold “a.” 紧跟删除线 “b”（需要 `__a.__~~b~~`）。`count` bold phrases followed
 *  by `tails` spots of bold "a." right before strikethrough "b" (they need `__a.__~~b~~`). */
function boldPhrasesAndTails(count: number, phrase: (index: number) => string, tails: number) {
  return (schema: Schema): ProseMirrorNode[] => {
    const nodes = boldPhrases(schema, count, phrase);
    for (let index = 0; index < tails; index++) {
      nodes.push(
        schema.text("a.", [schema.marks.bold.create()]),
        schema.text("b", [schema.marks.strike.create()]),
        schema.text(" "),
      );
    }
    nodes.push(schema.text("end"));
    return nodes;
  };
}

function boldPhrasesWritten(count: number, phrase: (index: number) => string): string {
  return Array.from({ length: count }, (_value, index) => `**${phrase(index)}** `).join("");
}

test("spelling searches that succeed never make a later paragraph fail to save", () => {
  // bold 以标点结尾、紧跟删除线：默认的 `**` 读不回，需要 `__`。Bold ending in punctuation right before
  // strikethrough: the default `**` does not read back, `__` does.
  const many = paragraphsWith(800, boldPhrasesAndTails(0, shortWord, 1));
  assert.equal(
    save(many),
    `${Array.from({ length: 800 }, () => "__a.__~~b~~ end").join("\n\n")}\n`,
  );
  const phrase = (index: number) => `a bold phrase number ${index} here`;
  const shapes: [number, (index: number) => string, number][] = [
    [20, phrase, 2],
    [60, sentence, 1],
  ];
  for (const [count, words, tails] of shapes) {
    const loaded = paragraphsWith(1, boldPhrasesAndTails(count, words, tails));
    assert.equal(
      save(loaded),
      `${boldPhrasesWritten(count, words)}${"__a.__~~b~~ ".repeat(tails)}end\n`,
    );
  }
  const code = paragraphsWith(1, (schema) => [
    ...boldPhrases(schema, 60, sentence),
    schema.text("x ", [schema.marks.bold.create()]),
    schema.text("a`*b", [schema.marks.bold.create(), schema.marks.code.create()]),
    schema.text(" end"),
  ]);
  assert.equal(save(code), `${boldPhrasesWritten(60, sentence)}__x \`\`a\`*b\`\`__ end\n`);
});

test("independent spots in one paragraph each get the spelling they need", () => {
  const tails = paragraphsWith(1, boldPhrasesAndTails(8, shortWord, 3));
  assert.equal(save(tails), `${boldPhrasesWritten(8, shortWord)}${"__a.__~~b~~ ".repeat(3)}end\n`);
  // 六处不同的写法问题，前面另有 6 个 bold 词（14 个可以选择写法的节点）。marked 寻找强调范围时把长度相同的反引号串
  // 配对，前两段行内代码因此用了不同的分隔符长度。
  // Six different spots, after 6 more bold words (14 nodes whose spelling can be chosen). marked pairs backtick runs
  // of equal length when it looks for emphasis ranges, so the first two code spans use different fence lengths.
  const spots = paragraphsWith(1, (schema) => {
    const { bold, italic, strike, code } = schema.marks;
    return [
      ...boldPhrases(schema, 6, shortWord),
      schema.text("a.", [bold.create()]),
      schema.text("b", [strike.create()]),
      schema.text(" q "),
      schema.text("x ", [bold.create()]),
      schema.text("a`*b", [bold.create(), code.create()]),
      schema.text(" r "),
      schema.text("x ", [italic.create()]),
      schema.text("a`_b", [italic.create(), code.create()]),
      schema.text(" s "),
      schema.text("x ", [strike.create()]),
      schema.text("a`~~b", [strike.create(), code.create()]),
      schema.text(" t "),
      schema.text("c.", [bold.create()]),
      schema.text("d", [strike.create()]),
      schema.text(" u "),
      schema.text("e!", [bold.create()]),
      schema.text("f", [strike.create()]),
    ];
  });
  assert.equal(
    save(spots),
    `${boldPhrasesWritten(6, shortWord)}__a.__~~b~~ q __x \`\`\`a\`*b\`\`\`__ r *x \`\`\`\`a\`_b\`\`\`\`* s ~x \`\`a\`~~b\`\`~ t __c.__~~d~~ u __e!__~~f~~\n`,
  );
});

test("bold, italic and strikethrough toggled on parts of one phrase save", () => {
  // [源文字, [起点, 终点, 标记]..., 写出的文字]。[source text, [from, to, mark]..., written text].
  const cases: [string, [number, number, string][], string][] = [
    [
      "a. b",
      [
        [0, 4, "italic"],
        [0, 2, "bold"],
        [2, 4, "strike"],
      ],
      "___a.__~~&#x20;b~~_",
    ],
    [
      "word",
      [
        [0, 4, "italic"],
        [0, 1, "strike"],
        [1, 3, "bold"],
      ],
      "_~~w~~**or**d_",
    ],
    [
      "Wait.a",
      [
        [4, 6, "italic"],
        [4, 5, "bold"],
        [5, 6, "strike"],
      ],
      "Wai&#x74;___.__~~a~~_",
    ],
  ];
  for (const [source, steps, expected] of cases) {
    const loaded = openMarkdown(`${source}\n`);
    for (const [from, to, mark] of steps) {
      loaded.editor
        .chain()
        .setTextSelection({ from: 1 + from, to: 1 + to })
        .toggleMark(mark)
        .run();
    }
    assert.equal(save(loaded), `${expected}\n`, source);
  }
  assert.equal(
    writeInline([text("a ", [bold, italic]), text("a", [italic, strike])]),
    "___a&#x20;__~~a~~_",
  );
});

/** 把第一个 paragraph 的内容换成单行的 `content` 后保存，返回写出的第一行；保存包含两项校验。
 *  Replaces the first paragraph's content with single-line `content` and saves (both verifications included);
 *  returns the first written line. */
function writeInline(content: JSONContent[], rest = ""): string {
  const loaded = openMarkdown(`Text.\n${rest}`);
  const { editor } = loaded;
  const paragraph = editor.state.doc.child(0);
  const nodes = content.map((node) => editor.schema.nodeFromJSON(node));
  editor.view.dispatch(editor.state.tr.replaceWith(1, 1 + paragraph.content.size, nodes));
  return save(loaded).split("\n")[0];
}

const bold = { type: "bold" };
const italic = { type: "italic" };
const strike = { type: "strike" };
const code = { type: "code" };
const link = { type: "link", attrs: { href: "/u" } };
const text = (value: string, marks?: JSONContent["marks"]): JSONContent => ({
  type: "text",
  text: value,
  marks,
});

test("inline output keeps marked from reading autolinks, broken delimiters or other constructs", () => {
  const cases: [string, JSONContent[], string, string?][] = [
    ["www text stays plain", [text("visit www.example.com now")], "visit www\\.example.com now"],
    ["http text stays plain", [text("visit http://a.example now")], "visit http\\://a.example now"],
    [
      "bold around punctuation between letters",
      [text("a"), text('"b"', [bold]), text("c")],
      '&#x61;**"b"**&#x63;',
    ],
    ["italic right after strikethrough", [text("a", [strike]), text("!b", [italic])], "~~a~~_!b_"],
    [
      "shortcut reference followed by (",
      [
        text("foo", [{ type: "link", attrs: { reference: "foo", referenceKind: "shortcut" } }]),
        text("(x)"),
      ],
      "[foo]\\(x)",
      "\n[foo]: /t\n",
    ],
    ["code with spaces at both ends", [text("x"), text(" y ", [code])], "x`  y  `"],
    ["code with a leading space", [text("x"), text(" y", [code])], "x` y`"],
    [
      "link text with a trailing space",
      [text("see "), text("foo ", [link]), text("bar")],
      "see [foo ](/u)bar",
    ],
    [
      "overlapping bold and italic",
      [text("a", [bold]), text("b", [bold, italic]), text("c", [italic])],
      "**&#x61;_&#x62;_**_c_",
    ],
    ["strikethrough ending with a space", [text("a ", [strike]), text("b")], "~~a&#x20;~~&#x62;"],
  ];
  for (const [name, content, expected, rest] of cases) {
    assert.equal(writeInline(content, rest), expected, name);
  }
});

test("spaces marked's trim() removes at the edges of headings, table cells and paragraphs are written as character references", () => {
  // macOS 的 Option+Space 输入 U+00A0。Option+Space types U+00A0 on macOS.
  const heading = openMarkdown("# Title\n\nb\n");
  heading.editor.view.dispatch(heading.editor.state.tr.insertText(" ", 1, 1));
  assert.equal(save(heading), "# &#xA0;Title\n\nb\n");

  const editCell = (source: string, text: string) => {
    const loaded = openMarkdown(source);
    const cell = find(
      loaded.editor,
      (node) => node.type.name === "paragraph" && node.textContent === "c",
    );
    replaceText(loaded.editor, cell.pos, cell.node, text);
    return save(loaded);
  };
  // 修改另一个单元格时整个表格重新输出。Editing another cell writes the whole table again.
  assert.equal(
    editCell("| a&nbsp;&nbsp; | b |\n| --- | --- |\n| c | d |\n", "C"),
    "| a &#xA0; | b |\n| --- | --- |\n| C | d |\n",
  );
  assert.equal(
    editCell("| a | b |\n| --- | --- |\n| c | d |\n", "c　"),
    "| a | b |\n| --- | --- |\n| c&#x3000; | d |\n",
  );

  // marked 去掉最后一个列表项结尾的空白。marked trims the end of the last list item.
  const item = openMarkdown("- a\n- b\n");
  const b = find(item.editor, (node) => node.type.name === "paragraph" && node.textContent === "b");
  item.editor.view.dispatch(item.editor.state.tr.insertText(" ", b.pos + 1 + b.node.content.size));
  assert.equal(save(item), "- a\n- b&#xA0;\n");
});

test("content verification tells whitespace inside a mark from whitespace beside it", () => {
  const loaded = openMarkdown("Text.\n");
  const { schema } = loaded.editor;
  const paragraph = (content: JSONContent[]) => ({
    type: "doc",
    content: [{ type: "paragraph", content }],
  });
  const inside = comparableDocument(paragraph([text("a ", [strike]), text("b")]), schema);
  const beside = comparableDocument(paragraph([text("a", [strike]), text(" b")]), schema);
  assert.equal(inside.eq(beside), false);
});

test("paragraph lines that would start a block are escaped and parse back as the same paragraph", () => {
  const starts = ["# a", "> a", "- a", "+ a", "* a", "1. a", "1) a", "---", "===", "```", "~~~"];
  for (const start of starts) {
    for (const afterBreak of [false, true]) {
      const loaded = openMarkdown("Text.\n");
      const paragraph = find(loaded.editor, (node) => node.type.name === "paragraph");
      const { schema } = loaded.editor;
      const content = afterBreak
        ? [schema.text("x"), schema.nodes.hardBreak.create(), schema.text(start)]
        : [schema.text(start)];
      loaded.editor.view.dispatch(
        loaded.editor.state.tr.replaceWith(
          paragraph.pos + 1,
          paragraph.pos + 1 + paragraph.node.content.size,
          content,
        ),
      );
      const written = save(loaded);
      const reparsed = reparse(written, loaded.editor);
      assert.equal(reparsed.childCount, 1, `${start} ${afterBreak}: ${JSON.stringify(written)}`);
      assert.ok(reparsed.child(0).eq(loaded.editor.state.doc.child(0)), `${start} ${afterBreak}`);
    }
  }
});
