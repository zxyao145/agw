// Markdown 渲染管线：Tiptap 解析 GFM，GitHub 规则的标题 id 与代码高亮，原始 HTML 显示为文字。
// The Markdown pipeline: Tiptap parses GFM, with GitHub-style heading ids and code highlighting; raw HTML shows as text.
import type { JSONContent } from "@tiptap/core";
import { isAllowedUri } from "@tiptap/extension-link";
import {
  TiptapStaticRenderer,
  type JSONMarkType,
  type JSONNodeType,
  type MarkProps,
  type NodeProps,
  type TiptapStaticRendererOptions,
} from "@tiptap/static-renderer/json/react";
import GithubSlugger from "github-slugger";
import { toJsxRuntime, type Jsx } from "hast-util-to-jsx-runtime";
import { common, createLowlight } from "lowlight";
import {
  createContext,
  createElement,
  Fragment,
  use,
  type ComponentPropsWithoutRef,
  type ElementType,
  type FC,
  type ReactElement,
  type ReactNode,
} from "react";
import { jsx, jsxs } from "react/jsx-runtime";

import { footnoteId, footnoteRefId } from "../markdown/markdown-extensions";
import { normalizeMarkdownDocument } from "../markdown/normalize-document";
import { parseMarkdown } from "../markdown/parse-markdown";
import { isMermaidLanguage } from "../shared/mermaid-render";

export type MarkdownImageProps = { src?: string; alt?: string; title?: string };

/** Mermaid 代码块：`source` 为源码，`children` 为源码的代码块。A Mermaid code block: `source` is the source and
 *  `children` the source's code block. */
export type MermaidBlockProps = { source: string; children: ReactNode };

/**
 * 与 react-markdown 的 components 作用相同；不传时输出普通的 a 与 img，Mermaid 代码块输出为代码块。
 * Like react-markdown's components; plain a and img by default, and Mermaid code blocks as code blocks.
 */
export type MarkdownComponents = {
  a?: ElementType<ComponentPropsWithoutRef<"a">>;
  img?: ElementType<MarkdownImageProps>;
  mermaid?: ElementType<MermaidBlockProps>;
};

type NodeComponentProps = NodeProps<JSONNodeType, ReactNode>;
type MarkComponentProps = MarkProps<JSONMarkType, ReactNode, JSONNodeType>;
type RendererOptions = TiptapStaticRendererOptions<ReactNode, JSONMarkType, JSONNodeType>;

const ComponentsContext = createContext<MarkdownComponents>({});
// 紧凑列表的列表项不输出 <p>，与 remark 相同。Items of a tight list output no <p>, as in remark.
const TightListContext = createContext(false);
const lowlight = createLowlight(common);
const FOOTNOTE_PREFIX = "user-content-";
const HEADING_TAGS = ["h1", "h2", "h3", "h4", "h5", "h6"] as const;

function childList(children: ReactNode): ReactNode[] {
  if (children === undefined) return [];
  return Array.isArray(children) ? children : [children];
}

function h(
  type: ElementType,
  props: Record<string, unknown> | null,
  ...children: ReactNode[]
): ReactElement {
  return createElement(type, props, ...children);
}

// props 顺序为 type、disabled、checked，renderToStaticMarkup 的输出与 remark-gfm 相同。
// Props go in the order type, disabled, checked so renderToStaticMarkup matches remark-gfm.
function taskCheckbox(checked: boolean): ReactElement {
  return h("input", { type: "checkbox", disabled: true, checked });
}

function isTableCell(node: JSONNodeType | undefined): boolean {
  return node?.type === "tableCell" || node?.type === "tableHeader";
}

function containsTasks(list: JSONNodeType): boolean {
  return Boolean(list.content?.some((item) => item.attrs?.checked != null));
}

function FootnoteBackReferences({ item }: { item: JSONNodeType }) {
  const { a = "a" } = use(ComponentsContext);
  const { label, number, refCount } = item.attrs as {
    label: string;
    number: string;
    refCount: number;
  };
  return h(
    Fragment,
    null,
    ...Array.from({ length: refCount }, (_, index) => [
      " ",
      h(
        a,
        {
          href: `#${FOOTNOTE_PREFIX}${footnoteRefId(label, index)}`,
          "data-footnote-backref": "",
          "aria-label": `Back to reference ${number}${index > 0 ? `-${index + 1}` : ""}`,
          className: "data-footnote-backref",
        },
        "↩",
        index > 0 ? h("sup", null, String(index + 1)) : null,
      ),
    ]).flat(),
  );
}

function DocNode({ children }: NodeComponentProps) {
  return h(Fragment, null, ...childList(children));
}

function TextNode({ node }: NodeComponentProps) {
  return node.text;
}

function ParagraphNode({ node, parent, children }: NodeComponentProps) {
  const tight = use(TightListContext);
  if (isTableCell(parent) || (parent?.type === "listItem" && tight)) {
    return h(Fragment, null, ...childList(children));
  }
  const checked =
    parent?.type === "listItem" && parent.content?.[0] === node ? parent.attrs?.checked : null;
  const backReferences =
    parent?.type === "footnoteItem" && parent.content?.at(-1) === node
      ? h(FootnoteBackReferences, { item: parent })
      : null;
  return h(
    "p",
    null,
    ...(checked == null ? [] : [taskCheckbox(checked), " "]),
    ...childList(children),
    backReferences,
  );
}

function HeadingNode({ node, children }: NodeComponentProps) {
  return h(
    HEADING_TAGS[(node.attrs?.level ?? 1) - 1],
    { id: node.attrs?.id ?? undefined },
    ...childList(children),
  );
}

function BlockquoteNode({ children }: NodeComponentProps) {
  return h("blockquote", null, ...childList(children));
}

function BulletListNode({ node, children }: NodeComponentProps) {
  return h(
    TightListContext,
    { value: Boolean(node.attrs?.tight) },
    h(
      "ul",
      containsTasks(node) ? { className: "contains-task-list" } : null,
      ...childList(children),
    ),
  );
}

function OrderedListNode({ node, children }: NodeComponentProps) {
  const start = node.attrs?.start ?? 1;
  return h(
    TightListContext,
    { value: Boolean(node.attrs?.tight) },
    h(
      "ol",
      {
        start: start === 1 ? undefined : start,
        className: containsTasks(node) ? "contains-task-list" : undefined,
      },
      ...childList(children),
    ),
  );
}

function ListItemNode({ node, children }: NodeComponentProps) {
  const tight = use(TightListContext);
  const checked = node.attrs?.checked;
  if (checked == null) return h("li", null, ...childList(children));
  // 紧凑列表中复选框直接放在列表项开头；非紧凑列表中由第一个 paragraph 输出。
  // In a tight list the checkbox starts the item; in a loose list the first paragraph outputs it.
  return h(
    "li",
    { className: "task-list-item" },
    ...(tight ? [taskCheckbox(checked), " "] : []),
    ...childList(children),
  );
}

function CodeBlockNode({ node }: NodeComponentProps) {
  const { mermaid } = use(ComponentsContext);
  const source = (node.content ?? []).map((child) => child.text ?? "").join("");
  const language: string | null = node.attrs?.language ?? null;
  // 与 mdast-util-to-hast 相同，代码末尾多一个换行。As in mdast-util-to-hast, the code ends with a newline.
  const block = codeBlock(`${source}\n`, language);
  return mermaid && isMermaidLanguage(language) ? h(mermaid, { source }, block) : block;
}

/** 不做语言自动识别，与 rehype-highlight 的默认行为一致。No language detection, as rehype-highlight's default. */
function codeBlock(text: string, language: string | null): ReactElement {
  if (language === null) return h("pre", null, h("code", null, text));
  if (!lowlight.registered(language)) {
    return h("pre", null, h("code", { className: `language-${language}` }, text));
  }
  return h(
    "pre",
    null,
    h(
      "code",
      { className: `hljs language-${language}` },
      toJsxRuntime(lowlight.highlight(language, text), {
        Fragment,
        jsx: jsx as Jsx,
        jsxs: jsxs as Jsx,
      }),
    ),
  );
}

function HorizontalRuleNode() {
  return h("hr", null);
}

function HardBreakNode() {
  return h("br", null);
}

function ImageNode({ node }: NodeComponentProps) {
  const { img = "img" } = use(ComponentsContext);
  return h(img, {
    src: node.attrs?.src ?? undefined,
    alt: node.attrs?.alt ?? undefined,
    title: node.attrs?.title ?? undefined,
  });
}

function TableNode({ node, children }: NodeComponentProps) {
  const rows = childList(children);
  const hasHead = Boolean(node.content?.[0]?.content?.every((cell) => cell.type === "tableHeader"));
  const body = hasHead ? rows.slice(1) : rows;
  return h(
    "table",
    null,
    hasHead ? h("thead", null, rows[0]) : null,
    body.length > 0 ? h("tbody", null, ...body) : null,
  );
}

function TableRowNode({ children }: NodeComponentProps) {
  return h("tr", null, ...childList(children));
}

function cellStyle(node: JSONNodeType) {
  return node.attrs?.align ? { textAlign: node.attrs.align } : undefined;
}

function TableHeaderNode({ node, children }: NodeComponentProps) {
  return h("th", { style: cellStyle(node) }, ...childList(children));
}

function TableCellNode({ node, children }: NodeComponentProps) {
  return h("td", { style: cellStyle(node) }, ...childList(children));
}

function FootnoteReferenceNode({ node }: NodeComponentProps) {
  const { a = "a" } = use(ComponentsContext);
  const { label, number, index } = node.attrs as { label: string; number: string; index: number };
  return h(
    "sup",
    null,
    h(
      a,
      {
        href: `#${FOOTNOTE_PREFIX}fn-${footnoteId(label)}`,
        id: `${FOOTNOTE_PREFIX}${footnoteRefId(label, index)}`,
        "data-footnote-ref": true,
        "aria-describedby": "footnote-label",
      },
      String(number),
    ),
  );
}

function FootnotesNode({ children }: NodeComponentProps) {
  return h(
    "section",
    { "data-footnotes": true, className: "footnotes" },
    h("h2", { className: "sr-only", id: "footnote-label" }, "Footnotes"),
    h("ol", null, ...childList(children)),
  );
}

function FootnoteItemNode({ node, children }: NodeComponentProps) {
  // 最后一个子节点不是 paragraph 时，返回链接放在列表项末尾，与 remark 相同。
  // When the last child is not a paragraph, the back references end the item, as in remark.
  const endsWithParagraph = node.content?.at(-1)?.type === "paragraph";
  return h(
    "li",
    { id: `${FOOTNOTE_PREFIX}fn-${footnoteId(node.attrs?.label ?? "")}` },
    ...childList(children),
    endsWithParagraph ? null : h(FootnoteBackReferences, { item: node }),
  );
}

const nodeMapping: RendererOptions["nodeMapping"] = {
  doc: DocNode,
  text: TextNode,
  paragraph: ParagraphNode,
  heading: HeadingNode,
  blockquote: BlockquoteNode,
  bulletList: BulletListNode,
  orderedList: OrderedListNode,
  listItem: ListItemNode,
  codeBlock: CodeBlockNode,
  horizontalRule: HorizontalRuleNode,
  hardBreak: HardBreakNode,
  image: ImageNode,
  table: TableNode,
  tableRow: TableRowNode,
  tableHeader: TableHeaderNode,
  tableCell: TableCellNode,
  footnoteReference: FootnoteReferenceNode,
  footnotes: FootnotesNode,
  footnoteItem: FootnoteItemNode,
};

function LinkMark({ mark, children }: MarkComponentProps) {
  const { a = "a" } = use(ComponentsContext);
  const href = typeof mark.attrs?.href === "string" ? mark.attrs.href : "";
  return h(
    a,
    { href: isAllowedUri(href) ? href : "", title: mark.attrs?.title ?? undefined },
    ...childList(children),
  );
}

const markMapping: RendererOptions["markMapping"] = {
  bold: ({ children }) => h("strong", null, ...childList(children)),
  italic: ({ children }) => h("em", null, ...childList(children)),
  strike: ({ children }) => h("del", null, ...childList(children)),
  code: ({ children }) => h("code", null, ...childList(children)),
  link: LinkMark,
};

// 映射函数会被当作组件类型使用，必须是模块级常量。The mapping functions serve as component types, so they are module-level constants.
const renderMarkdownJson = TiptapStaticRenderer<ReactNode, JSONMarkType, JSONNodeType>(
  ({ component, props: { children, ...props } }) =>
    createElement(component as FC<typeof props>, props, ...childList(children)),
  { nodeMapping, markMapping },
);

function headingText(node: JSONContent): string {
  if (node.type === "text") return node.text ?? "";
  if (node.type === "footnoteReference") return String(node.attrs?.number ?? "");
  return (node.content ?? []).map(headingText).join("");
}

/** 每个文档使用一个 GithubSlugger，规则与 rehype-slug 相同。One GithubSlugger per document, as rehype-slug does. */
function assignHeadingIds(document: JSONContent): JSONContent {
  const slugger = new GithubSlugger();
  const visit = (node: JSONContent): JSONContent => {
    if (node.type === "heading") {
      return { ...node, attrs: { ...node.attrs, id: slugger.slug(headingText(node)) } };
    }
    return node.content ? { ...node, content: node.content.map(visit) } : node;
  };
  return visit(document);
}

/** 解析并渲染一个 Markdown 文件。Parses and renders one Markdown file. */
export function renderMarkdown(text: string, components: MarkdownComponents = {}): ReactElement {
  const document = assignHeadingIds(normalizeMarkdownDocument(parseMarkdown(text)));
  return h(ComponentsContext, { value: components }, renderMarkdownJson({ content: document }));
}
