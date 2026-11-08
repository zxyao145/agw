import assert from "node:assert/strict";
import test from "node:test";

import { openMarkdown, readTestInputs } from "./test-editor";

const { splitFrontMatter } = await import("../markdown/front-matter");
const { withoutTrailingNewlines } = await import("../markdown/markdown-syntax");
const { recordBlock } = await import("../markdown/markdown-blocks");
const { lexEditorMarkdown } = await import("./markdown-parser");
const { MarkdownSourceMapError, footnoteContainerText, mapSourceBlocks, pairSourceBlocks } =
  await import("./source-map");

function nonSpaceTokens(body: string) {
  return lexEditorMarkdown(body).filter(
    (token) => token.type !== "space" && token.type !== "footnotes",
  );
}

test("every input file's block and list item ranges hold their token's raw text without trailing newlines", () => {
  for (const { name, source } of readTestInputs()) {
    const { prefix, body } = splitFrontMatter(source);
    const blocks = mapSourceBlocks(body, prefix.length);
    const tokens = nonSpaceTokens(body);
    assert.equal(blocks.length, tokens.length, name);
    blocks.forEach((block, index) => {
      const token = tokens[index];
      assert.equal(
        source.slice(block.range.from, block.range.to),
        withoutTrailingNewlines(token.raw),
        `${name} block ${index}`,
      );
      const items = (token as { items?: { raw: string }[] }).items;
      assert.deepEqual(
        block.items?.map((range) => source.slice(range.from, range.to)),
        items?.map((item) => withoutTrailingNewlines(item.raw)),
        `${name} items of block ${index}`,
      );
    });
  }
});

test("documents with and without footnotes map their blocks; the footnote container takes no position", () => {
  for (const [source, texts] of [
    ["hello\n", ["hello"]],
    ["\nhello\n", ["hello"]],
    ["a[^1]\n\n[^1]: note\n", ["a[^1]", "[^1]: note"]],
    ["", []],
  ] as const) {
    const blocks = mapSourceBlocks(source, 0);
    assert.deepEqual(
      blocks.map((block) => source.slice(block.range.from, block.range.to)),
      texts,
      JSON.stringify(source),
    );
  }
});

test("the baseline maps every block of the loaded document to its source range", () => {
  for (const { name, source } of readTestInputs()) {
    const { baseline } = openMarkdown(source);
    assert.equal(baseline.blocks.length, baseline.doc.childCount, name);
    baseline.doc.forEach((node, _offset, index) => {
      const { range } = baseline.blocks[index];
      const text = source.slice(range.from, range.to);
      if (node.type.name === "frontMatter") {
        assert.match(text, /^---\n[\s\S]*---$/, name);
      } else {
        assert.ok(text.length > 0, `${name} block ${index}`);
      }
    });
  }
});

test("blocks that do not pair with the source tokens throw MarkdownSourceMapError", () => {
  const blocks = mapSourceBlocks("- a\n- b\n", 0);
  const list = { type: "bulletList", content: [{ type: "listItem" }] };
  assert.throws(() => pairSourceBlocks([], blocks), MarkdownSourceMapError);
  assert.throws(() => pairSourceBlocks([recordBlock(list)], blocks), MarkdownSourceMapError);
  assert.deepEqual(
    pairSourceBlocks(
      [recordBlock({ ...list, content: [{ type: "listItem" }, { type: "listItem" }] })],
      blocks,
    ),
    [
      {
        range: { from: 0, to: 7 },
        items: [
          { from: 0, to: 3 },
          { from: 4, to: 7 },
        ],
      },
    ],
  );
});

test("only whitespace may follow the footnote container's caption", () => {
  assert.equal(footnoteContainerText("Footnotes"), "");
  assert.equal(footnoteContainerText("Footnotes\n"), "\n");
  assert.throws(() => footnoteContainerText("Footnotesx"), MarkdownSourceMapError);
  assert.throws(() => footnoteContainerText("Notes\n"), MarkdownSourceMapError);
});

test("an empty document gets one empty paragraph that has no source text", () => {
  const { baseline } = openMarkdown("");
  assert.equal(baseline.doc.childCount, 1);
  assert.deepEqual(baseline.blocks, [{ range: { from: 0, to: 0 }, items: null }]);
});
