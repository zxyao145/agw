import type { JSONContent } from "@tiptap/core";
import { MarkdownManager } from "@tiptap/markdown";
import { Marked, Tokenizer, type marked as globalMarked, type Tokens } from "marked";
import markedFootnote from "marked-footnote";
import { decodeString } from "micromark-util-decode-string";

import { markdownExtensions } from "./markdown-extensions";

// 行内 HTML 标签原样显示为文字：escape token 不经过 MarkdownManager 的 HTML 解析与实体解码。
// 普通文字的实体引用用 remark 使用的 decodeString 解码，再作为 escape token 交给 MarkdownManager。
// HTML 块保留为 html token，由 LiteralHtmlBlock 处理。
// Inline HTML tags show verbatim as text: escape tokens skip MarkdownManager's HTML parsing and entity decoding.
// Entity references in plain text are decoded with decodeString, the decoder remark uses, and passed on as escape tokens.
// HTML blocks stay html tokens for LiteralHtmlBlock.
const literalHtmlTokenizer = {
  tag(this: Tokenizer, src: string) {
    // 原实现维护 inLink 与 inRawBlock 状态。The original keeps the inLink and inRawBlock state.
    const token = Tokenizer.prototype.tag.call(this, src);
    return token && ({ type: "escape", raw: token.raw, text: token.raw } as unknown as Tokens.Tag);
  },
  inlineText(this: Tokenizer, src: string) {
    const token = Tokenizer.prototype.inlineText.call(this, src);
    return (
      token &&
      ({ type: "escape", raw: token.raw, text: decodeString(token.raw) } as unknown as Tokens.Text)
    );
  },
};

/**
 * 把 Markdown 解析为 Tiptap JSON。marked-footnote 的状态保存在每个实例中，因此每次解析都新建全部实例。
 * Parses Markdown into Tiptap JSON. marked-footnote keeps its state per instance, so every parse creates new instances.
 */
export function parseMarkdown(markdown: string): JSONContent {
  const marked = new Marked();
  marked.use({ tokenizer: literalHtmlTokenizer });
  // 选项的类型是全局 marked；MarkdownManager 只使用 Marked 实例同样提供的 Lexer、defaults 与 use。
  // The option is typed as the global marked; MarkdownManager uses only Lexer, defaults and use, which a Marked instance has.
  const manager = new MarkdownManager({
    marked: marked as unknown as typeof globalMarked,
    extensions: markdownExtensions,
  });
  // 在 MarkdownManager 之后注册，脚注 tokenizer 先执行，脚注容器位于根 lexer 的 tokens[0]。
  // Registered after MarkdownManager so the footnote tokenizer runs first and its container is the root lexer's tokens[0].
  marked.use(markedFootnote());
  return manager.parse(markdown);
}
