import type { JSONContent } from "@tiptap/core";
import type { Token } from "marked";

import { isRecordedBlock } from "../markdown/markdown-blocks";
import { withoutTrailingNewlines } from "../markdown/markdown-syntax";
import { lexEditorMarkdown } from "./markdown-parser";

// 块与原文位置（设计 5.1）。顶层 token 的 raw 依次拼接等于正文；块的位置为去掉末尾换行的 raw 所占的区间，
// 顶层列表的每个列表项同样处理。
// Blocks and their source positions (design section 5.1). The raw texts of the top-level tokens concatenate to the
// body; a block's range is its raw text without trailing newlines, and so is each item of a top-level list.

export class MarkdownSourceMapError extends Error {
  constructor(message: string) {
    super(message);
    this.name = "MarkdownSourceMapError";
  }
}

// markedFootnote() 默认的 description，即脚注容器的 raw。markedFootnote()'s default description, the container's raw.
const FOOTNOTES_CAPTION = "Footnotes";

export type SourceRange = { readonly from: number; readonly to: number };

export type SourceBlock = {
  readonly range: SourceRange;
  /** 顶层列表的列表项区间，其他块为 null。The item ranges of a top-level list; null for other blocks. */
  readonly items: readonly SourceRange[] | null;
};

type TokenBlock = SourceBlock & { readonly type: string };

/**
 * 脚注容器 raw 中属于原文的部分。marked-footnote 的脚注容器不论有没有脚注都会出现，它的 raw 开头是说明文字（默认的
 * description），这段文字不占原文的位置。容器在第一个 token 的位置；正文以一个换行开始时，marked 把这个换行并入前一个
 * token（容器）的 raw。说明文字之后只能是空白，否则抛出 MarkdownSourceMapError。
 * The part of the footnote container's raw that is source text. marked-footnote's container appears whether or not
 * there are footnotes; its raw starts with a caption (the default description), which takes no position in the
 * source. The container sits at the first token position; when the body starts with one newline, marked appends that
 * newline to the previous token's raw (the container's). Only whitespace may follow the caption, otherwise this throws
 * MarkdownSourceMapError.
 */
export function footnoteContainerText(raw: string): string {
  const rest = raw.slice(FOOTNOTES_CAPTION.length);
  if (!raw.startsWith(FOOTNOTES_CAPTION) || !/^\s*$/.test(rest)) {
    throw new MarkdownSourceMapError("The footnote container holds unexpected text.");
  }
  return rest;
}

/**
 * 取得正文中每个顶层块（除 space 以外的 token）的区间。offset 为正文在原文中的起点（front matter 的长度）。
 * Finds the range of every top-level block (each token other than space) in the body. offset is where the body
 * starts in the source (the front matter's length).
 */
export function mapSourceBlocks(body: string, offset: number): TokenBlock[] {
  // 脚注容器中属于原文的空白按 space 处理。The source whitespace inside the footnote container is handled as space.
  const tokens = lexEditorMarkdown(body).map((token) =>
    token.type === "footnotes"
      ? ({ type: "space", raw: footnoteContainerText(token.raw) } as Token)
      : token,
  );
  if (tokens.map((token) => token.raw).join("") !== body) {
    throw new MarkdownSourceMapError("The Markdown tokens don't cover the source text exactly.");
  }
  const blocks: TokenBlock[] = [];
  let position = offset;
  for (const token of tokens) {
    const start = position;
    position += token.raw.length;
    if (token.type === "space") continue;
    const text = withoutTrailingNewlines(token.raw);
    let items: SourceRange[] | null = null;
    if (token.type === "list") {
      const listItems = (token as { items: { raw: string }[] }).items;
      if (listItems.map((item) => item.raw).join("") !== text) {
        throw new MarkdownSourceMapError(
          "The list items don't cover the list's source text exactly.",
        );
      }
      items = [];
      let itemPosition = start;
      for (const item of listItems) {
        items.push({
          from: itemPosition,
          to: itemPosition + withoutTrailingNewlines(item.raw).length,
        });
        itemPosition += item.raw.length;
      }
    }
    blocks.push({ type: token.type, range: { from: start, to: start + text.length }, items });
  }
  return blocks;
}

/**
 * 文档中被记录过的顶层节点按顺序与块对应；列表节点的列表项与 token 的 items 对应。返回每个顶层节点对应的块
 * （没有记录的节点为 null）。
 * Pairs the document's recorded top-level nodes with the blocks in order, and a list node's items with the token's
 * items. Returns the block of each top-level node (null for unrecorded nodes).
 */
export function pairSourceBlocks(
  content: readonly JSONContent[],
  blocks: readonly TokenBlock[],
): (SourceBlock | null)[] {
  const recorded = content.filter((node) => isRecordedBlock(node));
  if (recorded.length !== blocks.length) {
    throw new MarkdownSourceMapError(
      `The document has ${recorded.length} blocks but the source has ${blocks.length}.`,
    );
  }
  let next = 0;
  return content.map((node) => {
    if (!isRecordedBlock(node)) return null;
    const block = blocks[next++];
    const isList = node.type === "bulletList" || node.type === "orderedList";
    if (
      isList !== (block.items !== null) ||
      (isList && node.content?.length !== block.items!.length)
    ) {
      throw new MarkdownSourceMapError(
        `The ${node.type} block doesn't match the ${block.type} token in the source.`,
      );
    }
    return { range: block.range, items: block.items };
  });
}
