import {
  Extension,
  flattenExtensions,
  Mark,
  Node,
  type AnyExtension,
  type JSONContent,
  type MarkdownParseHelpers,
  type MarkdownRendererHelpers,
  type MarkdownToken,
  type MarkdownTokenizer,
  type RenderContext,
} from "@tiptap/core";
import { CodeBlock } from "@tiptap/extension-code-block";
import { CodeBlockLowlight } from "@tiptap/extension-code-block-lowlight";
import { Heading, type HeadingOptions } from "@tiptap/extension-heading";
import { Image, type ImageOptions } from "@tiptap/extension-image";
import { Link } from "@tiptap/extension-link";
import { BulletList, ListItem, OrderedList, type ListItemOptions } from "@tiptap/extension-list";
import { Paragraph, type ParagraphOptions } from "@tiptap/extension-paragraph";
import {
  Table,
  TableCell,
  TableHeader,
  TableRow,
  type TableOptions,
} from "@tiptap/extension-table";
import { StarterKit } from "@tiptap/starter-kit";
import { common, createLowlight } from "lowlight";
import { Lexer } from "marked";

import { recordBlock } from "./markdown-blocks";
import {
  renderHeadingMarkdown,
  renderParagraphMarkdown,
  renderTableCellMarkdown,
  type SpellingBudget,
} from "./markdown-inline";

// Markdown 的 Tiptap 扩展。Preview 的扩展（markdownExtensions）只用于解析，渲染由 markdown-pipeline 完成；Edit 的扩展
// （editMarkdownExtensions，在文件后半部分）用于解析与输出（renderMarkdown，设计 5.5）。
// Tiptap extensions for Markdown. Preview's extensions (markdownExtensions) only parse, and markdown-pipeline renders
// the result; Edit's extensions (editMarkdownExtensions, in the second half of this file) parse and write
// (renderMarkdown, design section 5.5).

// 只含一张图片或 `&nbsp;` 的 paragraph 也保留为 paragraph。A paragraph holding only an image or `&nbsp;` stays a paragraph.
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

// 标题 id 由 assignHeadingIds 按 GitHub 规则生成。assignHeadingIds generates heading ids by GitHub's rules.
const MarkdownHeading = Heading.extend({
  addAttributes() {
    return { ...this.parent?.(), id: { default: null, rendered: false } };
  },
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
        href: `#user-content-fn-${footnoteId(node.attrs.label)}`,
        id: `user-content-${footnoteRefId(node.attrs.label, node.attrs.index)}`,
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
    ["h2", { class: "sr-only", id: "footnote-label" }, "Footnotes"],
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
  renderHTML: ({ node }) => ["li", { id: `user-content-fn-${footnoteId(node.attrs.label)}` }, 0],
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
    heading: false,
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
  MarkdownHeading,
  MarkdownBulletList,
  MarkdownOrderedList,
  MarkdownListItem,
  FencedCodeBlock,
  MarkdownTable,
  TableRow,
  TableHeader,
  TableCell,
  Image.configure({ inline: true }),
  FootnoteReference,
  FootnoteDefinition,
  Footnotes,
  FootnoteItem,
];

// ---------------------------------------------------------------------------------------------------------------
// Edit 的解析与输出。与 Preview 的差异见设计 5.2 与 5.5；块级扩展记录自己返回的节点（markdown-blocks.ts）。
// Edit's parsing and output. The differences from Preview follow design sections 5.2 and 5.5; block-level extensions
// record the nodes they return (markdown-blocks.ts).
// ---------------------------------------------------------------------------------------------------------------

type MarkdownNodeJson = JSONContent & { attrs?: Record<string, unknown> };

// 标记加到每个行内节点上（包括图片等行内节点），带链接的图片保留链接；同类标记不重复。hardBreak 没有标记。
// The mark goes on every inline node (images and other inline nodes included), so a linked image keeps its link;
// a mark type is not added twice. hardBreak carries no marks.
function applyMark(
  type: string,
  content: JSONContent[],
  attrs?: Record<string, unknown>,
): JSONContent[] {
  const mark = attrs ? { type, attrs } : { type };
  return content.map((node) =>
    node.type === "hardBreak" || node.marks?.some((existing) => existing.type === type)
      ? node
      : { ...node, marks: [...(node.marks ?? []), mark] },
  );
}

function textContent(node: JSONContent): string {
  return (node.content ?? []).map((child) => child.text ?? "").join("");
}

const editStarterKitExtensions = new Map(
  flattenExtensions([
    StarterKit.configure({
      document: false,
      paragraph: false,
      heading: false,
      codeBlock: false,
      bulletList: false,
      orderedList: false,
      listItem: false,
      link: false,
      underline: false,
      listKeymap: false,
      dropcursor: false,
      gapcursor: false,
      undoRedo: false,
      trailingNode: false,
    }),
  ]).map((extension) => [extension.name, extension]),
);

function starterKitNode(name: string): Node {
  return editStarterKitExtensions.get(name) as Node;
}

function starterKitMark(name: string): Mark {
  return editStarterKitExtensions.get(name) as Mark;
}

/**
 * 输出行内内容的 Edit 扩展的选项：本次保存的行内写法搜索（见 markdown-inline.ts 的 SpellingBudget）。输出只发生在
 * 保存中，createEditorRenderer 用 configure 设置它；没有设置时输出抛出错误。
 * The option of the Edit extensions that write inline content: this save's inline spelling search (see
 * SpellingBudget in markdown-inline.ts). Writing happens only while saving, and createEditorRenderer sets it with
 * configure; without it, writing throws.
 */
export type InlineWriterOptions = { spellingBudget: SpellingBudget | null };

/** 设置了 spellingBudget 选项的扩展名称。Names of the extensions that take the spellingBudget option. */
export const INLINE_WRITER_EXTENSIONS: ReadonlySet<string> = new Set([
  "paragraph",
  "heading",
  "listItem",
  "table",
  "image",
]);

function budgetOf(options: InlineWriterOptions): SpellingBudget {
  if (!options.spellingBudget) {
    throw new Error(
      "Inline Markdown is written only by the renderer createEditorRenderer makes, which sets spellingBudget.",
    );
  }
  return options.spellingBudget;
}

const EditParagraph = MarkdownParagraph.extend<ParagraphOptions & InlineWriterOptions>({
  addOptions() {
    return { ...this.parent!(), spellingBudget: null };
  },
  parseMarkdown: (token: MarkdownToken, helpers: MarkdownParseHelpers) =>
    recordBlock(
      helpers.createNode("paragraph", undefined, helpers.parseInline(token.tokens ?? [])),
    ),
  renderMarkdown(node: MarkdownNodeJson) {
    return renderParagraphMarkdown(node.content ?? [], budgetOf(this.options));
  },
});

const EditHeading = Heading.extend<HeadingOptions & InlineWriterOptions>({
  addOptions() {
    return { ...this.parent!(), spellingBudget: null };
  },
  addAttributes() {
    return { ...this.parent?.(), style: { default: "atx", rendered: false } };
  },
  parseMarkdown: (token: MarkdownToken, helpers: MarkdownParseHelpers) =>
    recordBlock(
      helpers.createNode(
        "heading",
        { level: token.depth, style: /^ {0,3}#/.test(token.raw ?? "") ? "atx" : "setext" },
        helpers.parseInline(token.tokens ?? []),
      ),
    ),
  // style 为 setext 时 1、2 级的单行标题用 setext 写法。A setext-style single-line level 1 or 2 heading keeps the setext
  // form.
  renderMarkdown(node: MarkdownNodeJson) {
    return renderHeadingMarkdown(
      Number(node.attrs?.level ?? 1),
      node.attrs?.style === "setext",
      node.content ?? [],
      budgetOf(this.options),
    );
  },
});

const EditBlockquote = starterKitNode("blockquote").extend({
  parseMarkdown(token: MarkdownToken, helpers: MarkdownParseHelpers) {
    return recordBlock(this.parent?.(token, helpers) as JSONContent);
  },
  // 只含空白的行（例如代码块中的行）保留空白。Whitespace-only lines (such as code lines) keep their whitespace.
  renderMarkdown: (node: MarkdownNodeJson, helpers: MarkdownRendererHelpers) =>
    (node.content ?? [])
      .map((child, index) =>
        helpers.renderChild!(child, index)
          .split("\n")
          .map((line) => (line ? `> ${line}` : ">"))
          .join("\n"),
      )
      .join("\n>\n"),
});

const EditHorizontalRule = starterKitNode("horizontalRule").extend({
  parseMarkdown: (_token: MarkdownToken, helpers: MarkdownParseHelpers) =>
    recordBlock(helpers.createNode("horizontalRule")),
});

/**
 * 列表符号（设计 5.3）：第一项原文的第一个非空白字符（无序列表），或数字之后的字符（有序列表）。
 * The list symbol (design section 5.3): the first non-whitespace character of the first item's source (bullet
 * list), or the character after the number (ordered list).
 */
export function listSymbolOf(ordered: boolean, firstItemRaw: string): string {
  const raw = firstItemRaw.trimStart();
  return ordered ? /^\d+([.)])/.exec(raw)![1] : raw[0];
}

function firstItemRaw(token: MarkdownToken): string {
  return token.items?.[0]?.raw ?? "";
}

function renderListMarkdown(node: MarkdownNodeJson, helpers: MarkdownRendererHelpers): string {
  return (node.content ?? [])
    .map((item, index) => helpers.renderChild!(item, index))
    .join(node.attrs?.tight === false ? "\n\n" : "\n");
}

const EditBulletList = BulletList.extend({
  addAttributes() {
    return {
      ...this.parent?.(),
      ...tightAttribute,
      marker: { default: "-", rendered: false },
    };
  },
  parseMarkdown: (token: MarkdownToken, helpers: MarkdownParseHelpers) => {
    if (token.ordered) return [];
    return recordBlock(
      helpers.createNode(
        "bulletList",
        { tight: !token.loose, marker: listSymbolOf(false, firstItemRaw(token)) },
        helpers.parseChildren(token.items ?? []),
      ),
    );
  },
  renderMarkdown: renderListMarkdown,
});

const EditOrderedList = MarkdownOrderedList.extend({
  addAttributes() {
    return { ...this.parent?.(), delimiter: { default: ".", rendered: false } };
  },
  parseMarkdown: (token: MarkdownToken, helpers: MarkdownParseHelpers) => {
    if (!token.ordered) return [];
    return recordBlock(
      helpers.createNode(
        "orderedList",
        {
          start: typeof token.start === "number" ? token.start : 1,
          tight: !token.loose,
          delimiter: listSymbolOf(true, firstItemRaw(token)),
        },
        helpers.parseChildren(token.items ?? []),
      ),
    );
  },
  renderMarkdown: renderListMarkdown,
});

function renderListItemMarkdown(
  node: MarkdownNodeJson,
  helpers: MarkdownRendererHelpers,
  context: RenderContext,
  budget: SpellingBudget,
): string {
  const list = (context.meta?.parentAttrs ?? {}) as Record<string, unknown>;
  const prefix =
    context.parentType === "orderedList"
      ? `${Number(list.start ?? 1) + context.index}${String(list.delimiter ?? ".")}`
      : String(list.marker ?? "-");
  const tight = list.tight !== false;
  const [first, ...rest] = node.content ?? [];
  const firstText = renderParagraphMarkdown(first?.content ?? [], budget);
  const checkbox = node.attrs?.checked == null ? "" : node.attrs.checked ? "[x] " : "[ ] ";
  const head = firstText || checkbox ? `${prefix} ${checkbox}${firstText}` : prefix;
  // 紧凑的列表项中，缩进代码不能紧跟在上一个块之后，改用围栏。
  // In a tight item an indented code block cannot directly follow the previous block, so it is fenced.
  const blocks = rest.map((child, index) =>
    helpers.renderChild!(
      tight && child.type === "codeBlock"
        ? { ...child, attrs: { ...child.attrs, fenced: true } }
        : child,
      index + 1,
    ),
  );
  const separator = tight ? "\n" : "\n\n";
  // 第一个 paragraph 为空时其余子块从下一行开始：列表项最多以一个空行开始。
  // With an empty first paragraph the other blocks start on the next line: an item starts with at most one blank line.
  const body = blocks.length
    ? `${head}${firstText || checkbox ? separator : "\n"}${blocks.join(separator)}`
    : head;
  const indent = " ".repeat(prefix.length + 1);
  return body
    .split("\n")
    .map((line, index) => (index === 0 || !line ? line : indent + line))
    .join("\n");
}

const EditListItem = MarkdownListItem.extend<ListItemOptions & InlineWriterOptions>({
  addOptions() {
    return { ...this.parent!(), spellingBudget: null };
  },
  renderMarkdown(node: MarkdownNodeJson, helpers: MarkdownRendererHelpers, context: RenderContext) {
    return renderListItemMarkdown(node, helpers, context, budgetOf(this.options));
  },
});

function renderCodeBlockMarkdown(node: MarkdownNodeJson): string {
  const text = textContent(node);
  const info = String(node.attrs?.info ?? "");
  const lines = text.split("\n");
  // 缩进写法不能带 info，第一行与最后一行也不能是空行。The indented form carries no info and cannot start or end
  // with a blank line.
  if (node.attrs?.fenced === false && !info && lines[0].trim() && lines.at(-1)!.trim()) {
    return lines.map((line) => (line ? `    ${line}` : line)).join("\n");
  }
  const recorded = typeof node.attrs?.fence === "string" ? node.attrs.fence : "```";
  // 反引号围栏的 info 不能含反引号。The info of a backtick fence cannot contain backticks.
  const char = recorded[0] === "~" || info.includes("`") ? "~" : "`";
  const longest = Math.max(
    0,
    ...(text.match(char === "`" ? /`+/g : /~+/g) ?? []).map((run) => run.length),
  );
  const fence = char.repeat(Math.max(3, recorded[0] === char ? recorded.length : 0, longest + 1));
  return `${fence}${info.replaceAll("\\", "\\\\")}\n${text}${text ? "\n" : ""}${fence}`;
}

const EditCodeBlock = CodeBlockLowlight.extend({
  addAttributes() {
    return {
      ...this.parent?.(),
      info: { default: "", rendered: false },
      fenced: { default: true, rendered: false },
      fence: { default: null, rendered: false },
    };
  },
  // extend 复制父扩展的配置，父扩展的 addProseMirrorPlugins 会通过 this.parent 再调用一次自己，加入两个高亮插件；
  // 这里只返回父扩展的插件。
  // extend copies the parent's config, so the parent's addProseMirrorPlugins would run once more through this.parent
  // and add the highlight plugin twice; this returns the parent's plugins only.
  addProseMirrorPlugins() {
    return this.parent?.() ?? [];
  },
  parseMarkdown: (token: MarkdownToken, helpers: MarkdownParseHelpers) => {
    const info = String(token.lang ?? "");
    const fenced = token.codeBlockStyle !== "indented";
    return recordBlock(
      helpers.createNode(
        "codeBlock",
        {
          language: info.split(/\s/, 1)[0] || null,
          info,
          fenced,
          fence: fenced ? /^ {0,3}(`{3,}|~{3,})/.exec(token.raw ?? "")![1] : null,
        },
        token.text ? [helpers.createTextNode(token.text)] : [],
      ),
    );
  },
  renderMarkdown: renderCodeBlockMarkdown,
}).configure({ lowlight: createLowlight(common) });

const TABLE_ALIGN_DELIMITERS: Record<string, string> = {
  left: ":---",
  center: ":---:",
  right: "---:",
};

function renderTableMarkdown(node: MarkdownNodeJson, budget: SpellingBudget): string {
  const rows = node.content ?? [];
  const renderRow = (row: JSONContent) =>
    `${(row.content ?? [])
      .map((cell) => `| ${renderTableCellMarkdown(cell.content?.[0]?.content ?? [], budget)} `)
      .join("")}|`;
  const delimiter = `${(rows[0]?.content ?? [])
    .map((cell) => `| ${TABLE_ALIGN_DELIMITERS[String(cell.attrs?.align)] ?? "---"} `)
    .join("")}|`;
  return [renderRow(rows[0]), delimiter, ...rows.slice(1).map(renderRow)].join("\n");
}

const EditTable = MarkdownTable.extend<TableOptions & InlineWriterOptions>({
  addOptions() {
    return { ...this.parent!(), spellingBudget: null };
  },
  parseMarkdown(token: MarkdownToken, helpers: MarkdownParseHelpers) {
    return recordBlock(this.parent?.(token, helpers) as JSONContent);
  },
  renderMarkdown(node: MarkdownNodeJson) {
    return renderTableMarkdown(node, budgetOf(this.options));
  },
});

// 单元格的内容为一个 paragraph。A cell holds exactly one paragraph.
const EditTableCell = TableCell.extend({ content: "paragraph" });
const EditTableHeader = TableHeader.extend({ content: "paragraph" });

// 与链接相同：reference 与 referenceKind 是写法属性；引用式图片的 src 与 title 为 null，显示与对比时由文档中的定义解析。
// As for links: reference and referenceKind are syntax attributes; a reference image's src and title are null and
// are resolved from the document's definitions for display and comparison.
const EditImage = Image.configure({ inline: true }).extend<ImageOptions & InlineWriterOptions>({
  addOptions() {
    return { ...this.parent!(), spellingBudget: null };
  },
  addAttributes() {
    return {
      ...this.parent?.(),
      reference: { default: null, rendered: false },
      referenceKind: { default: null, rendered: false },
    };
  },
  parseMarkdown: (token: MarkdownToken, helpers: MarkdownParseHelpers) => {
    const reference = (token.reference as string | undefined) ?? null;
    return helpers.createNode("image", {
      src: reference === null ? token.href : null,
      title: reference === null ? token.title : null,
      alt: token.text,
      reference,
      referenceKind: reference === null ? null : token.referenceKind,
    });
  },
  renderMarkdown(node: MarkdownNodeJson) {
    return renderParagraphMarkdown([node], budgetOf(this.options));
  },
});

function markParse(type: string) {
  return (token: MarkdownToken, helpers: MarkdownParseHelpers) =>
    applyMark(type, helpers.parseInline(token.tokens ?? []));
}

const EditBold = starterKitMark("bold").extend({ parseMarkdown: markParse("bold") });
const EditItalic = starterKitMark("italic").extend({ parseMarkdown: markParse("italic") });
const EditStrike = starterKitMark("strike").extend({ parseMarkdown: markParse("strike") });

// 行内代码可以与 bold、链接等标记同时存在（`**`x`**`、`` [`x`](u) ``），只排除自身。
// Inline code can coexist with bold, links and other marks (`**`x`**`, `` [`x`](u) ``); it excludes only itself.
const EditCode = starterKitMark("code").extend({
  excludes: "code",
  parseMarkdown: (token: MarkdownToken) =>
    applyMark("code", [{ type: "text", text: String(token.text ?? "") }]),
});

// reference 与 referenceKind 是写法属性；引用式链接的 href 为 null，显示与对比时由文档中的定义解析。
// reference and referenceKind are syntax attributes; a reference link's href is null and is resolved from the
// document's definitions for display and comparison.
const EditLink = Link.extend({
  addAttributes() {
    return {
      ...this.parent?.(),
      reference: { default: null, rendered: false },
      referenceKind: { default: null, rendered: false },
    };
  },
  parseMarkdown: (token: MarkdownToken, helpers: MarkdownParseHelpers) => {
    const reference = (token.reference as string | undefined) ?? null;
    return applyMark("link", helpers.parseInline(token.tokens ?? []), {
      href: reference === null ? token.href : null,
      title: token.title || null,
      reference,
      referenceKind: reference === null ? null : token.referenceKind,
    });
  },
}).configure({ openOnClick: false, autolink: false, linkOnPaste: false });

/**
 * Edit 的 Markdown 扩展（编辑器专用的节点在 src/editor/editor-nodes.ts）。Preview 不使用。
 * Edit's Markdown extensions (editor-only nodes live in src/editor/editor-nodes.ts). Preview does not use them.
 */
export const editMarkdownExtensions: AnyExtension[] = [
  editStarterKitExtensions.get("text")!,
  editStarterKitExtensions.get("hardBreak")!,
  EditParagraph,
  EditHeading,
  EditBlockquote,
  EditHorizontalRule,
  EditBulletList,
  EditOrderedList,
  EditListItem,
  EditCodeBlock,
  EditTable,
  TableRow,
  EditTableHeader,
  EditTableCell,
  EditImage,
  EditBold,
  EditItalic,
  EditStrike,
  EditCode,
  EditLink,
];
