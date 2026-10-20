import { Marked, type Tokenizer } from "marked";
import markedFootnote from "marked-footnote";

// 链接定义、脚注定义与标签的写法，Edit 的解析、规范化与输出共用。
// Syntax of link definitions, footnote definitions and labels, shared by Edit's parsing, normalization and output.

export type LinkDefinition = { href: string; title: string | null };

const TRAILING_NEWLINES = /\n+$/;

/** 按 marked 的规则对比标签：转为小写并合并连续空白。Compares labels by marked's rule: lowercase, whitespace collapsed. */
export function normalizeLinkLabel(label: string): string {
  return label.replace(/\s+/g, " ").toLowerCase();
}

/**
 * 图片说明在原文中的写法：marked 读取说明时只去掉方括号前的反斜杠，这里加回去。
 * How an image's alt text is written in the source: marked unescapes only brackets when it reads the alt, so this
 * escapes them again.
 */
export function escapeImageAlt(alt: string): string {
  return alt.replace(/[[\]]/g, "\\$&");
}

/** 去掉 token 原文末尾的换行。Strips the trailing newlines of a token's raw text. */
export function withoutTrailingNewlines(raw: string): string {
  return raw.replace(TRAILING_NEWLINES, "");
}

let definitionTokenizer: Tokenizer | null = null;

// marked 原有的 def tokenizer；构造 Lexer 时 tokenizer 才得到 rules。
// marked's own def tokenizer; it gets its rules when a Lexer is constructed.
function getDefinitionTokenizer(): Tokenizer {
  if (!definitionTokenizer) {
    const marked = new Marked();
    definitionTokenizer = new marked.Lexer(marked.defaults).options.tokenizer as Tokenizer;
  }
  return definitionTokenizer;
}

/**
 * 用 marked 原有的 def tokenizer 解析一个定义块的文字，整段文字都是一个定义时返回标签、地址与标题。
 * Parses a definition block's text with marked's def tokenizer; returns the label, destination and title when the
 * whole text is one definition.
 */
export function parseLinkDefinition(text: string): (LinkDefinition & { label: string }) | null {
  const token = getDefinitionTokenizer().def(text);
  if (!token || withoutTrailingNewlines(token.raw) !== text) return null;
  return { label: token.tag, href: token.href, title: token.title ?? null };
}

/**
 * 文字开头会被读作链接定义或脚注定义。The start of the text would read as a link or footnote definition.
 */
export function startsWithDefinition(text: string): boolean {
  return /^\[\^[^\]\n]+\]:/.test(text) || Boolean(getDefinitionTokenizer().def(text));
}

/**
 * 每次调用新建一组 marked-footnote 实例，读取脚注定义块的标签。标签按原样对比。
 * Creates fresh marked-footnote instances per call to read the labels of footnote definition blocks. Labels compare
 * as written.
 */
export function createFootnoteLabelReader(): (text: string) => string | null {
  const marked = new Marked();
  marked.use(markedFootnote());
  return (text) => {
    const token = new marked.Lexer(marked.defaults)
      .lex(text)
      .find((candidate) => candidate.type === "footnote");
    if (!token || withoutTrailingNewlines(token.raw) !== text) return null;
    return (token as unknown as { label: string }).label;
  };
}
