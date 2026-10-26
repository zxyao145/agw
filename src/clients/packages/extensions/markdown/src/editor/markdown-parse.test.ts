import assert from "node:assert/strict";
import test from "node:test";

import type { JSONContent } from "@tiptap/core";

import { openMarkdown } from "./test-editor";

const { FrontMatterError } = await import("../markdown/front-matter");

function load(source: string): JSONContent[] {
  return openMarkdown(source).editor.getJSON().content ?? [];
}

function textOf(node: JSONContent): string {
  return node.type === "text" ? (node.text ?? "") : (node.content ?? []).map(textOf).join("");
}

const FENCE = "```";

test("HTML blocks, every link definition and footnote definitions become raw blocks without trailing newlines", () => {
  const blocks = load(
    "<div>\n  a\n</div>\n\n[a]: /x\n[a]: /y\n\n[b]: /z\n\n\nText[^n]\n\n[^n]: Note\n    more\n",
  );
  assert.deepEqual(
    blocks.map((node) => [node.type, node.attrs?.kind, textOf(node)]),
    [
      ["rawBlock", "html", "<div>\n  a\n</div>"],
      ["rawBlock", "definition", "[a]: /x"],
      ["rawBlock", "definition", "[a]: /y"],
      ["rawBlock", "definition", "[b]: /z"],
      ["paragraph", undefined, "Text"],
      ["rawBlock", "footnote", "[^n]: Note\n    more"],
    ],
  );
});

test("inline tags become rawInline nodes and the three <br> spellings become hard breaks", () => {
  const [paragraph] = load('a <span class="x">b</span> c<br>d<br/>e<BR />f');
  assert.deepEqual(
    paragraph.content?.map((node) =>
      node.type === "text" ? node.text : [node.type, node.attrs?.raw],
    ),
    [
      "a ",
      ["rawInline", '<span class="x">'],
      "b",
      ["rawInline", "</span>"],
      " c",
      ["hardBreak", undefined],
      "d",
      ["hardBreak", undefined],
      "e",
      ["hardBreak", undefined],
      "f",
    ],
  );
});

test("a definition right after a paragraph line stays in the paragraph", () => {
  const blocks = load("Text\n[a]: /x\n\n[a]\n");
  assert.equal(blocks[0].type, "paragraph");
  assert.equal(textOf(blocks[0]), "Text\n[a]: /x");
  assert.equal(textOf(blocks[1]), "[a]");
});

test("reference links record their label and spelling, and get their destination from the definitions", () => {
  const [paragraph] = load(
    '[full][Foo] [collapsed][] [shortcut] [inline](/i)\n\n[foo]: /f "T"\n[collapsed]: /c\n[shortcut]: /s\n',
  );
  const links = paragraph.content
    ?.filter((node) => node.marks?.some((mark) => mark.type === "link"))
    .map((node) => {
      const attrs = node.marks!.find((mark) => mark.type === "link")!.attrs!;
      return [node.text, attrs.reference, attrs.referenceKind, attrs.href, attrs.title];
    });
  assert.deepEqual(links, [
    ["full", "Foo", "full", "/f", "T"],
    ["collapsed", "collapsed", "collapsed", "/c", null],
    ["shortcut", "shortcut", "shortcut", "/s", null],
    ["inline", null, null, "/i", null],
  ]);
});

test("reference images record their label and spelling, and get their source from the definitions", () => {
  const [paragraph] = load('![a][Logo] ![logo][] ![logo] ![b](/i.png "I")\n\n[logo]: /l.png "L"\n');
  const images = paragraph.content
    ?.filter((node) => node.type === "image")
    .map(({ attrs }) => [
      attrs?.alt,
      attrs?.reference,
      attrs?.referenceKind,
      attrs?.src,
      attrs?.title,
    ]);
  assert.deepEqual(images, [
    ["a", "Logo", "full", "/l.png", "L"],
    ["logo", "logo", "collapsed", "/l.png", "L"],
    ["logo", "logo", "shortcut", "/l.png", "L"],
    ["b", null, null, "/i.png", "I"],
  ]);
});

test("code blocks keep the whole info string, the fence and whether they are fenced", () => {
  const blocks = load(
    `${FENCE}ts title=x\na\n${FENCE}\n\n~~~~\nb\n~~~~\n\n    c\n\n${FENCE}\nd\n${FENCE}\n`,
  );
  assert.deepEqual(
    blocks.map((node) => [
      node.attrs?.info,
      node.attrs?.language,
      node.attrs?.fenced,
      node.attrs?.fence,
    ]),
    [
      ["ts title=x", "ts", true, FENCE],
      ["", null, true, "~~~~"],
      ["", null, false, null],
      ["", null, true, FENCE],
    ],
  );
});

test("headings record their style and lists their marker or delimiter", () => {
  const blocks = load("# atx\n\nsetext\n===\n\n* a\n\n- b\n\n+ c\n\n1. d\n\n1) e\n");
  assert.deepEqual(
    blocks.map((node) => node.attrs?.style ?? node.attrs?.marker ?? node.attrs?.delimiter),
    ["atx", "setext", "*", "-", "+", ".", ")"],
  );
});

test("front matter becomes a frontMatter node and a later --- stays a thematic break", () => {
  const blocks = load("---\ntitle: x\n---\n\ntext\n\n---\n\nmore\n");
  assert.deepEqual(
    blocks.map((node) => [node.type, textOf(node)]),
    [
      ["frontMatter", "title: x"],
      ["paragraph", "text"],
      ["horizontalRule", ""],
      ["paragraph", "more"],
    ],
  );
});

test("invalid YAML front matter throws", () => {
  assert.throws(
    () => openMarkdown("---\ntitle: [unclosed\n---\n\ntext\n"),
    (error: unknown) =>
      error instanceof FrontMatterError &&
      error.message.startsWith("Front matter isn't valid YAML: "),
  );
});

test("inline code may carry other marks and an image keeps the link around it", () => {
  const [paragraph] = load("**`x`** [`y`](/u) [![i](i.png)](/l)");
  const marks = paragraph.content?.map((node) => [
    node.type,
    node.marks?.map((mark) => mark.type).sort(),
  ]);
  assert.deepEqual(marks, [
    ["text", ["bold", "code"]],
    ["text", undefined],
    ["text", ["code", "link"]],
    ["text", undefined],
    ["image", ["link"]],
  ]);
});
