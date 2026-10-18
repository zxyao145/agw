import {
  Extension,
  Node,
  type AnyExtension,
  type JSONContent,
  type MarkdownParseHelpers,
  type MarkdownToken,
  type MarkdownTokenizer,
} from "@tiptap/core";
import { CodeBlock } from "@tiptap/extension-code-block";
import { Image } from "@tiptap/extension-image";
import { BulletList, ListItem, OrderedList } from "@tiptap/extension-list";
import { BlockMath, InlineMath } from "@tiptap/extension-mathematics";
import { Paragraph } from "@tiptap/extension-paragraph";
import { Table, TableCell, TableHeader, TableRow } from "@tiptap/extension-table";
import { StarterKit } from "@tiptap/starter-kit";
import { Lexer } from "marked";

// 只用于解析 Markdown 的 Tiptap 扩展；渲染由 markdown-node-mapping 完成。
// Tiptap extensions used only to parse Markdown; markdown-node-mapping renders the result.

// 只含一张图片或 `&nbsp;` 的 paragraph 也保留为 paragraph，流式输出中结构不变。
// A paragraph holding only an image or `&nbsp;` stays a paragraph, so its structure holds while streaming.
const MarkdownParagraph = Paragraph.extend({
  parseMarkdown: (token: MarkdownToken, helpers: MarkdownParseHelpers) =>
    helpers.createNode("paragraph", undefined, helpers.parseInline(token.tokens ?? [])),
});

// HTML 块显示为一个 paragraph 中的原文。token 保持 html 类型：marked 会把后续 paragraph 合并进类型为 paragraph 的前一个 token。
// An HTML block shows as its source text in one paragraph. The token stays an html token: marked merges a following
// paragraph into a preceding paragraph-typed token.
const LiteralHtmlBlock = Extension.create({
  name: "literalHtmlBlock",
  markdownTokenName: "html",
  parseMarkdown: (token: MarkdownToken, helpers: MarkdownParseHelpers) =>
    helpers.createNode("paragraph", undefined, [
      { type: "text", text: (token.raw ?? "").replace(/\n+$/, "") },
    ]),
});

function withTightness(result: unknown, token: MarkdownToken) {
  if (!result || Array.isArray(result)) return result;
  const node = result as JSONContent;
  return { ...node, attrs: { ...node.attrs, tight: !token.loose } };
}

const tightAttribute = { tight: { default: true, rendered: false } };

const MarkdownBulletList = BulletList.extend({
  addAttributes() {
    return { ...this.parent?.(), ...tightAttribute };
  },
  parseMarkdown(token: MarkdownToken, helpers: MarkdownParseHelpers) {
    return withTightness(this.parent?.(token, helpers), token) as JSONContent;
  },
});

const MarkdownOrderedList = OrderedList.extend({
  addAttributes() {
    return { ...this.parent?.(), ...tightAttribute };
  },
  // 列表项交给 ListItem 解析，处理任务项与 paragraph。List items go through ListItem for tasks and paragraphs.
  parseMarkdown: (token: MarkdownToken, helpers: MarkdownParseHelpers) => {
    if (!token.ordered) return [];
    const start = typeof token.start === "number" ? token.start : 1;
    return helpers.createNode(
      "orderedList",
      { start, tight: !token.loose },
      helpers.parseChildren(token.items ?? []),
    );
  },
  // 父类的 tokenizer 在块级阶段直接 inline 解析列表项，绕过 marked 的列表规则与脚注；null 让 MarkdownManager 不注册它。
  // The parent tokenizer inline-parses items during the block pass, bypassing marked's list rules and footnotes;
  // null keeps MarkdownManager from registering it.
  markdownTokenizer: null as unknown as undefined,
});

function withoutCheckbox(tokens: MarkdownToken[]): MarkdownToken[] {
  // 紧凑项的复选框在开头；非紧凑项的复选框在第一个 paragraph 开头。
  // A tight item starts with the checkbox; a loose item has it at the start of its first paragraph.
  if (tokens[0]?.type === "checkbox") return tokens.slice(1);
  const index = tokens.findIndex((token) => token.type === "paragraph");
  const paragraph = tokens[index];
  if (paragraph?.tokens?.[0]?.type !== "checkbox") return tokens;
  return tokens.with(index, { ...paragraph, tokens: paragraph.tokens.slice(1) });
}

const MarkdownListItem = ListItem.extend({
  addAttributes() {
    return { ...this.parent?.(), checked: { default: null, rendered: false } };
  },
  parseMarkdown: (token: MarkdownToken, helpers: MarkdownParseHelpers) => {
    const tokens = token.task ? withoutCheckbox(token.tokens ?? []) : (token.tokens ?? []);
    const [first, ...rest] = tokens;
    const content =
      first?.type === "text"
        ? [
            helpers.createNode("paragraph", undefined, helpers.parseInline(first.tokens ?? [])),
            ...helpers.parseChildren(rest),
          ]
        : helpers.parseChildren(tokens);
    return helpers.createNode("listItem", { checked: token.task ? token.checked : null }, content);
  },
});

const FencedCodeBlock = CodeBlock.extend({
  addAttributes() {
    return { ...this.parent?.(), fenced: { default: true, rendered: false } };
  },
  parseMarkdown: (token: MarkdownToken, helpers: MarkdownParseHelpers) =>
    helpers.createNode(
      "codeBlock",
      {
        language: (token.lang ?? "").split(/\s/, 1)[0] || null,
        fenced: token.codeBlockStyle !== "indented",
      },
      token.text ? [helpers.createTextNode(token.text)] : [],
    ),
});

const TABLE_DELIMITER_PATTERN = /^[ \t|:]*-[ \t|:-]*$/;

/** 第二行是 GFM 表格分隔行。The second line is a GFM delimiter row. */
function hasTableDelimiter(src: string): boolean {
  const firstEnd = src.indexOf("\n");
  if (firstEnd < 0) return false;
  const secondEnd = src.indexOf("\n", firstEnd + 1);
  const delimiter = src.slice(firstEnd + 1, secondEnd < 0 ? undefined : secondEnd);
  return TABLE_DELIMITER_PATTERN.test(delimiter) && delimiter.includes("|");
}

const tableTokenizer = Table.config.markdownTokenizer as MarkdownTokenizer;

// 原 start 与 tokenize 每次都处理剩余全文，耗时随文本长度按平方增长（tiptap issue #8458）；修复发布后删除这两个替换。
// The original start and tokenize process the whole remaining text on every call, growing quadratically
// (tiptap issue #8458); remove both replacements once the fix is released.
const MarkdownTable = Table.extend({
  markdownTokenizer: {
    ...tableTokenizer,
    start: (src: string) =>
      hasTableDelimiter(src) && src.slice(0, src.indexOf("\n")).includes("|") ? 0 : -1,
    tokenize: (src, tokens, helper) => {
      if (!hasTableDelimiter(src)) return undefined;
      // marked 用这条规则决定表格范围，原 tokenize 只处理当前这一张表格。
      // marked bounds a table with this rule, so the original tokenize handles this table only.
      const match = Lexer.rules.block.gfm.table.exec(src);
      return match ? tableTokenizer.tokenize(match[0], tokens, helper) : undefined;
    },
  },
});

// 行首（0–3 个空格）的 `$$`，结尾的 `$$` 之后到行尾只有空白。
// `$$` at a line start (0–3 spaces), with only whitespace after the closing `$$` up to the line end.
const BLOCK_MATH_PATTERN = / {0,3}\$\$([^$]+)\$\$[ \t]*(?=\n|$)/y;

function matchBlockMath(src: string, index: number) {
  BLOCK_MATH_PATTERN.lastIndex = index;
  return BLOCK_MATH_PATTERN.exec(src);
}

const MarkdownBlockMath = BlockMath.extend({
  markdownTokenizer: {
    name: "blockMath",
    level: "block",
    // marked 用 start 寻找 paragraph 的中断点，传入的文本从 paragraph 的第二个字符开始，因此只看换行之后的行首。
    // marked uses start to find where a paragraph breaks; the text begins at the paragraph's second
    // character, so only line starts after a newline count.
    start: (src: string) => {
      for (let index = src.indexOf("$$"); index >= 0; index = src.indexOf("$$", index + 2)) {
        const lineStart = src.lastIndexOf("\n", index) + 1;
        if (lineStart > 0 && index - lineStart <= 3 && matchBlockMath(src, lineStart)) {
          return lineStart;
        }
      }
      return -1;
    },
    tokenize: (src: string) => {
      const match = matchBlockMath(src, 0);
      return match ? { type: "blockMath", raw: match[0], latex: match[1].trim() } : undefined;
    },
  },
});

// `$..$` 与 `$$..$$`，开闭符号数量相同，与 remark-math 一致。`$..$` and `$$..$$` with matching runs, as in remark-math.
const INLINE_MATH_PATTERN = /^(\$\$?)([^$]+)\1(?!\$)/;

const MarkdownInlineMath = InlineMath.extend({
  markdownTokenizer: {
    name: "inlineMath",
    level: "inline",
    start: (src: string) => src.indexOf("$"),
    tokenize: (src: string) => {
      const match = INLINE_MATH_PATTERN.exec(src);
      return match ? { type: "inlineMath", raw: match[0], latex: match[2].trim() } : undefined;
    },
  },
});

type FootnoteToken = { label: string; refs: { id: string }[]; content: MarkdownToken[] };

const FootnoteReference = Node.create({
  name: "footnoteReference",
  group: "inline",
  inline: true,
  atom: true,
  addAttributes: () => ({
    label: { default: "" },
    number: { default: "" },
    index: { default: 0 },
  }),
  renderHTML: ({ node }) => [
    "sup",
    {},
    [
      "a",
      {
        href: `#fn-${footnoteId(node.attrs.label)}`,
        id: footnoteRefId(node.attrs.label, node.attrs.index),
        "data-footnote-ref": "",
        "aria-describedby": "footnote-label",
      },
      String(node.attrs.number),
    ],
  ],
  markdownTokenName: "footnoteRef",
  parseMarkdown: (token: MarkdownToken, helpers: MarkdownParseHelpers) =>
    helpers.createNode("footnoteReference", {
      label: token.label,
      number: token.id,
      index: token.index,
    }),
});

// 定义所在位置不输出内容，内容由脚注区域显示。A definition outputs nothing in place; the footnotes section shows it.
const FootnoteDefinition = Extension.create({
  name: "footnoteDefinition",
  markdownTokenName: "footnote",
  parseMarkdown: () => [],
});

const Footnotes = Node.create({
  name: "footnotes",
  group: "block",
  content: "footnoteItem+",
  renderHTML: () => [
    "section",
    { "data-footnotes": "", class: "footnotes" },
    ["h2", { id: "footnote-label", class: "sr-only" }, "Footnotes"],
    ["ol", 0],
  ],
  markdownTokenName: "footnotes",
  parseMarkdown: (token: MarkdownToken, helpers: MarkdownParseHelpers) =>
    helpers.createNode(
      "footnotes",
      undefined,
      (token.items as FootnoteToken[]).map((item) =>
        helpers.createNode(
          "footnoteItem",
          { label: item.label, number: item.refs[0].id, refCount: item.refs.length },
          helpers.parseChildren(item.content),
        ),
      ),
    ),
});

const FootnoteItem = Node.create({
  name: "footnoteItem",
  content: "block+",
  addAttributes: () => ({
    label: { default: "" },
    number: { default: "" },
    refCount: { default: 1 },
  }),
  renderHTML: ({ node }) => ["li", { id: `fn-${footnoteId(node.attrs.label)}` }, 0],
});

/** 脚注 id 中的标签部分，与 remark 相同使用小写。The label part of footnote ids, lowercased as in remark. */
export function footnoteId(label: string): string {
  return encodeURIComponent(label.toLowerCase());
}

/** 第 index 次引用（从 0 开始）的 id。The id of the index-th reference (zero-based). */
export function footnoteRefId(label: string, index: number): string {
  return `fnref-${footnoteId(label)}${index > 0 ? `-${index + 1}` : ""}`;
}

export const markdownExtensions: AnyExtension[] = [
  StarterKit.configure({
    // `++text++` 会把 `C++ and C++` 识别为下划线。`++text++` would turn `C++ and C++` into an underline.
    underline: false,
    paragraph: false,
    codeBlock: false,
    bulletList: false,
    orderedList: false,
    listItem: false,
    // 以下扩展只用于编辑。The following are editing-only.
    listKeymap: false,
    dropcursor: false,
    gapcursor: false,
    undoRedo: false,
    trailingNode: false,
  }),
  MarkdownParagraph,
  LiteralHtmlBlock,
  MarkdownBulletList,
  MarkdownOrderedList,
  MarkdownListItem,
  FencedCodeBlock,
  MarkdownTable,
  TableRow,
  TableHeader,
  TableCell,
  Image.configure({ inline: true }),
  MarkdownInlineMath,
  MarkdownBlockMath,
  FootnoteReference,
  FootnoteDefinition,
  Footnotes,
  FootnoteItem,
];
