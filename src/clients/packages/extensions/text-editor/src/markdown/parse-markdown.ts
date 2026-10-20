import type { JSONContent } from "@tiptap/core";
import { MarkdownManager } from "@tiptap/markdown";
import {
  Lexer,
  Marked,
  Tokenizer,
  type Links,
  type marked as globalMarked,
  type Token,
  type TokenizerExtension,
  type TokenizerThis,
  type Tokens,
} from "marked";
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

export type ReferenceKind = "full" | "collapsed" | "shortcut";

const BR_TAG = /^<br(?: ?\/)?>$/i;

// Edit 的行内 tokenizer：`<br>` 的三种写法为 br token，其他标签为 rawInline token；普通文字与 Preview 相同；
// 引用式链接与图片记录标签与写法。
// Edit's inline tokenizers: the three `<br>` spellings become br tokens and other tags rawInline tokens; plain text
// is the same as Preview; reference links and images record their label and spelling.
export const editTokenizers = {
  tag(this: Tokenizer, src: string) {
    // 原实现维护 inLink 与 inRawBlock 状态。The original keeps the inLink and inRawBlock state.
    const token = Tokenizer.prototype.tag.call(this, src);
    if (!token) return token;
    const type = BR_TAG.test(token.raw) ? "br" : "rawInline";
    return { type, raw: token.raw, text: token.raw } as unknown as Tokens.Tag;
  },
  inlineText: literalHtmlTokenizer.inlineText,
  reflink(this: Tokenizer, src: string, links: Links) {
    const token = Tokenizer.prototype.reflink.call(this, src, links);
    if (token?.type !== "link" && token?.type !== "image") return token;
    // 与原实现相同的顺序：先完整写法，再 collapsed 与 shortcut。The same order as the original: full first.
    const full = Lexer.rules.inline.gfm.reflink.exec(src);
    const nolink = full ? null : Lexer.rules.inline.gfm.nolink.exec(src);
    const label = full ? full[2] : nolink![1];
    const referenceKind: ReferenceKind = full
      ? "full"
      : nolink![0].endsWith("[]")
        ? "collapsed"
        : "shortcut";
    // 标签按原样保存，查找定义与对比时按 marked 的规则处理。The label is kept as written; looking up definitions and
    // comparing apply marked's rule.
    const recorded: (Tokens.Link | Tokens.Image) & {
      reference: string;
      referenceKind: ReferenceKind;
    } = { ...token, reference: label, referenceKind };
    return recorded;
  },
};

/**
 * 链接定义：前一个 token 不是 paragraph 或 text 时由 marked 原有的 def tokenizer 识别，每个定义（包括重复的）都返回
 * token，标签第一次出现时登记链接；前一个 token 是 paragraph 时不处理，由 marked 按原规则并入 paragraph。
 * Link definitions: when the previous token is not a paragraph or text, marked's own def tokenizer recognizes them;
 * every definition (duplicates included) returns a token and the first occurrence of a label registers the link.
 * After a paragraph the extension declines and marked merges the definition into the paragraph as before.
 */
export const definitionExtension: TokenizerExtension = {
  name: "definition",
  level: "block",
  tokenizer(this: TokenizerThis, src: string, tokens: Token[]) {
    const previous = tokens.at(-1);
    if (previous?.type === "paragraph" || previous?.type === "text") return undefined;
    const token = (this.lexer.options.tokenizer as Tokenizer).def(src);
    if (!token) return undefined;
    const links = this.lexer.tokens.links;
    if (!links[token.tag]) links[token.tag] = { href: token.href, title: token.title };
    return { ...token, type: "definition" };
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
