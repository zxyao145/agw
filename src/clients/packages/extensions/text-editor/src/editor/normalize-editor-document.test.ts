import assert from "node:assert/strict";
import test from "node:test";

import type { JSONContent } from "@tiptap/core";

import { openMarkdown } from "./test-editor";

const { normalizeEditorDocument } = await import("./normalize-editor-document");
const { markdownDefinitionsKey } = await import("./markdown-definitions");

function textOf(node: JSONContent): string {
  return node.type === "text" ? (node.text ?? "") : (node.content ?? []).map(textOf).join("");
}

function blocks(source: string): JSONContent[] {
  return openMarkdown(source).editor.getJSON().content ?? [];
}

test("list items whose first child is not a paragraph start with an empty paragraph", () => {
  const [list] = blocks("- a\n-\n- ```\n  code\n  ```\n- - nested\n");
  assert.deepEqual(
    list.content?.map((item) => item.content?.map((child) => [child.type, textOf(child)])),
    [
      [["paragraph", "a"]],
      [["paragraph", ""]],
      [
        ["paragraph", ""],
        ["codeBlock", "code"],
      ],
      [
        ["paragraph", ""],
        ["bulletList", "nested"],
      ],
    ],
  );
});

test("an empty blockquote and an empty document keep one empty paragraph", () => {
  assert.deepEqual(blocks(">\n"), [{ type: "blockquote", content: [{ type: "paragraph" }] }]);
  assert.deepEqual(blocks("\n\n"), [{ type: "paragraph" }]);
  assert.deepEqual(
    normalizeEditorDocument({ type: "doc", content: [{ type: "blockquote", content: [] }] }),
    { type: "doc", content: [{ type: "blockquote", content: [{ type: "paragraph" }] }] },
  );
});

test("empty raw blocks are removed like empty paragraphs", () => {
  const empty = { type: "rawBlock", attrs: { kind: "html" } };
  const text = { type: "paragraph", content: [{ type: "text", text: "a" }] };
  assert.deepEqual(
    normalizeEditorDocument({
      type: "doc",
      content: [
        text,
        empty,
        { type: "blockquote", content: [empty] },
        {
          type: "bulletList",
          attrs: { tight: true, marker: "-" },
          content: [{ type: "listItem", attrs: { checked: null }, content: [text, empty] }],
        },
      ],
    }),
    {
      type: "doc",
      content: [
        text,
        { type: "blockquote", content: [{ type: "paragraph" }] },
        {
          type: "bulletList",
          attrs: { tight: true, marker: "-" },
          content: [{ type: "listItem", attrs: { checked: null }, content: [text] }],
        },
      ],
    },
  );
  assert.deepEqual(normalizeEditorDocument({ type: "doc", content: [empty] }), {
    type: "doc",
    content: [{ type: "paragraph" }],
  });
});

test("an empty task item becomes the text [ ] or [x]", () => {
  const document = normalizeEditorDocument({
    type: "doc",
    content: [
      {
        type: "bulletList",
        attrs: { tight: true, marker: "-" },
        content: [
          { type: "listItem", attrs: { checked: false }, content: [{ type: "paragraph" }] },
          { type: "listItem", attrs: { checked: true }, content: [{ type: "paragraph" }] },
        ],
      },
    ],
  });
  assert.deepEqual(
    document.content?.[0].content?.map((item) => [item.attrs?.checked, textOf(item)]),
    [
      [null, "[ ]"],
      [null, "[x]"],
    ],
  );
});

test("a footnote reference without a definition becomes text", () => {
  const document = normalizeEditorDocument({
    type: "doc",
    content: [
      {
        type: "paragraph",
        content: [
          { type: "text", text: "a" },
          { type: "footnoteReference", attrs: { label: "x" } },
          { type: "footnoteReference", attrs: { label: "y" } },
        ],
      },
      {
        type: "rawBlock",
        attrs: { kind: "footnote" },
        content: [{ type: "text", text: "[^y]: Y" }],
      },
    ],
  });
  assert.deepEqual(document.content?.[0].content, [
    { type: "text", text: "a[^x]" },
    { type: "footnoteReference", attrs: { label: "y" } },
  ]);
});

test("a reference link gets its destination from the definition and becomes text without one", () => {
  const link = (kind: string) => ({
    type: "text",
    text: "Foo",
    marks: [
      { type: "link", attrs: { href: null, title: null, reference: "Foo", referenceKind: kind } },
    ],
  });
  const paragraph = (kind: string) => ({ type: "paragraph", content: [link(kind)] });
  const definition = {
    type: "rawBlock",
    attrs: { kind: "definition" },
    content: [{ type: "text", text: '[foo]: /target "T"' }],
  };
  const resolved = normalizeEditorDocument({
    type: "doc",
    content: [paragraph("full"), definition],
  });
  assert.deepEqual(resolved.content?.[0].content?.[0].marks?.[0].attrs, {
    href: "/target",
    title: "T",
    reference: "Foo",
    referenceKind: "full",
  });
  for (const [kind, text] of [
    ["full", "[Foo][Foo]"],
    ["collapsed", "[Foo][]"],
    ["shortcut", "[Foo]"],
  ]) {
    const unresolved = normalizeEditorDocument({ type: "doc", content: [paragraph(kind)] });
    assert.deepEqual(unresolved.content?.[0].content, [{ type: "text", text }], kind);
  }
});

test("a reference image gets its source from the definition and becomes text without one", () => {
  const image = (kind: string): JSONContent => ({
    type: "image",
    attrs: { src: null, alt: "x", title: null, reference: "Logo", referenceKind: kind },
    marks: [{ type: "bold" }],
  });
  const paragraph = (kind: string) => ({ type: "paragraph", content: [image(kind)] });
  const definition = {
    type: "rawBlock",
    attrs: { kind: "definition" },
    content: [{ type: "text", text: '[logo]: /l.png "L"' }],
  };
  const resolved = normalizeEditorDocument({
    type: "doc",
    content: [paragraph("full"), definition],
  });
  assert.deepEqual(resolved.content?.[0].content?.[0].attrs, {
    src: "/l.png",
    alt: "x",
    title: "L",
    reference: "Logo",
    referenceKind: "full",
  });
  for (const [kind, text] of [
    ["full", "![x][Logo]"],
    ["collapsed", "![x][]"],
    ["shortcut", "![x]"],
  ]) {
    const unresolved = normalizeEditorDocument({ type: "doc", content: [paragraph(kind)] });
    assert.deepEqual(
      unresolved.content?.[0].content,
      [{ type: "text", text, marks: [{ type: "bold" }] }],
      kind,
    );
  }
  // 说明按行内 Markdown 解析，图片的标记包住全部文字。The alt parses as inline Markdown, and the image's marks enclose
  // all of the text.
  const emphasized = normalizeEditorDocument({
    type: "doc",
    content: [
      {
        type: "paragraph",
        content: [{ ...image("full"), attrs: { ...image("full").attrs, alt: "*x*" } }],
      },
    ],
  });
  assert.deepEqual(
    emphasized.content?.[0].content?.map((node) => [
      node.text,
      node.marks?.map((mark) => mark.type),
    ]),
    [
      ["![", ["bold"]],
      ["x", ["bold", "italic"]],
      ["][Logo]", ["bold"]],
    ],
  );
});

test("a list with a second paragraph or a table in an item is not tight", () => {
  const [paragraphs] = blocks("- a\n\n  b\n- c\n");
  assert.equal(paragraphs.attrs?.tight, false);
  const document = normalizeEditorDocument({
    type: "doc",
    content: [
      {
        type: "bulletList",
        attrs: { tight: true, marker: "-" },
        content: [
          {
            type: "listItem",
            attrs: { checked: null },
            content: [
              { type: "paragraph", content: [{ type: "text", text: "a" }] },
              { type: "paragraph", content: [{ type: "text", text: "b" }] },
            ],
          },
          {
            type: "listItem",
            attrs: { checked: null },
            content: [{ type: "paragraph", content: [{ type: "text", text: "c" }] }],
          },
        ],
      },
    ],
  });
  assert.equal(document.content?.[0].attrs?.tight, false);
});

test("while editing, footnote references lose their link to a deleted definition and links follow the definitions", () => {
  const { editor } = openMarkdown("Text[^n] and [site].\n\n[site]: /old\n\n[^n]: Note\n");
  const definitions = () => markdownDefinitionsKey.getState(editor.state)!;
  assert.deepEqual(definitions().links.get("site"), { href: "/old", title: null });
  const rawBlock = (kind: string) => {
    const found: { pos: number; size: number }[] = [];
    editor.state.doc.forEach((node, offset) => {
      if (node.attrs.kind === kind) found.push({ pos: offset, size: node.nodeSize });
    });
    return found[0];
  };
  const firstParagraph = () => (editor.getJSON() as JSONContent).content![0];
  // 修改定义块后，显示地址随之更新，链接标记不变。After editing the definition the displayed destination follows,
  // and the link mark is unchanged.
  const linkMarks = () =>
    JSON.stringify(firstParagraph().content?.find((node) => node.text === "site")?.marks);
  const before = linkMarks();
  const definition = rawBlock("definition");
  editor.view.dispatch(
    editor.state.tr.insertText(
      "[site]: /new",
      definition.pos + 1,
      definition.pos + definition.size - 1,
    ),
  );
  assert.deepEqual(definitions().links.get("site"), { href: "/new", title: null });
  assert.equal(linkMarks(), before);
  // 删除脚注定义后，引用改为文字。Deleting the footnote definition turns the reference into text.
  const footnote = rawBlock("footnote");
  editor.view.dispatch(editor.state.tr.delete(footnote.pos, footnote.pos + footnote.size));
  assert.equal(textOf(firstParagraph()), "Text[^n] and site.");
  assert.equal(
    firstParagraph().content!.some((node) => node.type === "footnoteReference"),
    false,
  );
});
