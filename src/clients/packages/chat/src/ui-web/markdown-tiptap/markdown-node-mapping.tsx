"use client";

import {
  TiptapStaticRenderer,
  type JSONMarkType,
  type JSONNodeType,
  type MarkProps,
  type NodeProps,
  type TiptapStaticRendererOptions,
} from "@tiptap/static-renderer/json/react";
import katex from "katex";
import React from "react";
import {
  MarkdownCodeBlock,
  MarkdownImage,
  MarkdownLink,
  MarkdownTable,
} from "../components/message/renders/markdown-elements";
import { footnoteId, footnoteRefId } from "./markdown-extensions";

// 映射函数会被当作组件类型使用，必须是模块级常量，引用保持稳定，流式输出时已有元素保持挂载。
// The mapping functions serve as component types; they stay module-level constants so existing elements
// remain mounted while streaming.

type NodeComponentProps = NodeProps<JSONNodeType, React.ReactNode>;
type MarkComponentProps = MarkProps<JSONMarkType, React.ReactNode, JSONNodeType>;

const HEADING_TAGS = ["h1", "h2", "h3", "h4", "h5", "h6"] as const;

/** 脚注 id 的前缀，避免同一页面的多条消息 id 重复。The footnote id prefix that keeps ids unique across messages. */
export const FootnoteIdPrefixContext = React.createContext("");

// 子元素逐个按位置传入，内容增长时已有子元素的位置不变。Children go in one by one, so existing ones keep their position.
function childList(children: React.ReactNode): React.ReactNode[] {
  if (children === undefined) return [];
  return Array.isArray(children) ? children : [children];
}

function h(
  type: React.ElementType,
  props: Record<string, unknown> | null,
  ...children: React.ReactNode[]
): React.ReactElement {
  return React.createElement(type, props, ...children);
}

function scrollToFootnoteTarget(event: React.MouseEvent<HTMLAnchorElement>) {
  event.preventDefault();
  const id = event.currentTarget.getAttribute("href")?.slice(1);
  const target = id ? document.getElementById(id) : null;
  target?.scrollIntoView({ block: "nearest" });
  target?.focus({ preventScroll: true });
}

function isTableCell(node: JSONNodeType | undefined): boolean {
  return node?.type === "tableCell" || node?.type === "tableHeader";
}

function FootnoteBackReferences({ item }: { item: JSONNodeType }) {
  const prefix = React.useContext(FootnoteIdPrefixContext);
  const { label, number, refCount } = item.attrs as {
    label: string;
    number: string;
    refCount: number;
  };
  return h(
    React.Fragment,
    null,
    ...Array.from({ length: refCount }, (_, index) => [
      " ",
      h(
        "a",
        {
          href: `#${prefix}${footnoteRefId(label, index)}`,
          "data-footnote-backref": "",
          "aria-label": `Back to reference ${number}${index > 0 ? `-${index + 1}` : ""}`,
          className: "data-footnote-backref",
          onClick: scrollToFootnoteTarget,
        },
        "↩",
        index > 0 ? h("sup", null, String(index + 1)) : null,
      ),
    ]).flat(),
  );
}

function DocNode({ children }: NodeComponentProps) {
  return h(React.Fragment, null, ...childList(children));
}

function TextNode({ node }: NodeComponentProps) {
  return node.text;
}

function ParagraphNode({ node, parent, children }: NodeComponentProps) {
  if (isTableCell(parent)) return h(React.Fragment, null, ...childList(children));
  const checked =
    parent?.type === "listItem" && parent.content?.[0] === node ? parent.attrs?.checked : null;
  const backReferences =
    parent?.type === "footnoteItem" && parent.content?.at(-1) === node
      ? h(FootnoteBackReferences, { item: parent })
      : null;
  // 复选框与空格各占固定位置。The checkbox and its space each hold a fixed position.
  return h(
    "p",
    { className: "msg-content-md-p" },
    checked == null ? null : h("input", { type: "checkbox", disabled: true, checked }),
    checked == null ? null : " ",
    ...childList(children),
    backReferences,
  );
}

function HeadingNode({ node, children }: NodeComponentProps) {
  return h(HEADING_TAGS[(node.attrs?.level ?? 1) - 1], null, ...childList(children));
}

function BlockquoteNode({ children }: NodeComponentProps) {
  return h("blockquote", null, ...childList(children));
}

function BulletListNode({ children }: NodeComponentProps) {
  return h("ul", { className: "msg-content-md-ul" }, ...childList(children));
}

function OrderedListNode({ node, children }: NodeComponentProps) {
  const start = node.attrs?.start ?? 1;
  return h(
    "ol",
    { className: "msg-content-md-ol", start: start === 1 ? undefined : start },
    ...childList(children),
  );
}

function ListItemNode({ children }: NodeComponentProps) {
  return h("li", { className: "msg-content-md-li" }, ...childList(children));
}

function CodeBlockNode({ node, children }: NodeComponentProps) {
  // 与 mdast-util-to-hast 相同，<code> 的文字末尾多一个换行；复制的内容不含它。
  // As in mdast-util-to-hast, the <code> text ends with a newline that copying leaves out.
  if (!node.attrs?.fenced) {
    return h(
      "pre",
      { className: "msg-content-md-code overflow-x-auto agw-scrollbar" },
      h("code", { className: "msg-content-md-code" }, ...childList(children), "\n"),
    );
  }
  const language = node.attrs.language ?? "plain";
  const code = (node.content ?? []).map((child) => child.text ?? "").join("");
  return h(
    MarkdownCodeBlock,
    { code, language },
    h(
      "code",
      { className: `msg-content-md-code language-${language}` },
      ...childList(children),
      "\n",
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
  return h(MarkdownImage, {
    src: node.attrs?.src,
    alt: node.attrs?.alt,
    title: node.attrs?.title,
  });
}

function TableNode({ node, children }: NodeComponentProps) {
  const rows = childList(children);
  const hasHead = Boolean(node.content?.[0]?.content?.every((cell) => cell.type === "tableHeader"));
  return h(
    MarkdownTable,
    null,
    hasHead ? h("thead", null, rows[0]) : null,
    h("tbody", null, ...(hasHead ? rows.slice(1) : rows)),
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

function useKatexHtml(latex: string, displayMode: boolean): string {
  return React.useMemo(
    () => katex.renderToString(latex, { displayMode, throwOnError: false }),
    [latex, displayMode],
  );
}

function InlineMathNode({ node }: NodeComponentProps) {
  const html = useKatexHtml(node.attrs?.latex ?? "", false);
  return h("span", { dangerouslySetInnerHTML: { __html: html } });
}

function BlockMathNode({ node }: NodeComponentProps) {
  const html = useKatexHtml(node.attrs?.latex ?? "", true);
  return h("div", { dangerouslySetInnerHTML: { __html: html } });
}

function FootnoteReferenceNode({ node }: NodeComponentProps) {
  const prefix = React.useContext(FootnoteIdPrefixContext);
  const { label, number, index } = node.attrs as { label: string; number: string; index: number };
  return h(
    "sup",
    null,
    h(
      "a",
      {
        href: `#${prefix}fn-${footnoteId(label)}`,
        id: `${prefix}${footnoteRefId(label, index)}`,
        "data-footnote-ref": true,
        "aria-describedby": `${prefix}footnote-label`,
        onClick: scrollToFootnoteTarget,
      },
      String(number),
    ),
  );
}

function FootnotesNode({ children }: NodeComponentProps) {
  const prefix = React.useContext(FootnoteIdPrefixContext);
  return h(
    "section",
    { className: "msg-content-md-footnotes", "data-footnotes": true },
    h("h2", { id: `${prefix}footnote-label`, className: "sr-only" }, "Footnotes"),
    h("ol", null, ...childList(children)),
  );
}

function FootnoteItemNode({ node, children }: NodeComponentProps) {
  const prefix = React.useContext(FootnoteIdPrefixContext);
  // 最后一个子节点不是 paragraph 时，返回链接放在列表项末尾，与 remark 相同。
  // When the last child is not a paragraph, the back references end the item, as in remark.
  const endsWithParagraph = node.content?.at(-1)?.type === "paragraph";
  return h(
    "li",
    { id: `${prefix}fn-${footnoteId(node.attrs?.label ?? "")}`, tabIndex: -1 },
    ...childList(children),
    endsWithParagraph ? null : h(FootnoteBackReferences, { item: node }),
  );
}

type RendererOptions = TiptapStaticRendererOptions<React.ReactNode, JSONMarkType, JSONNodeType>;

export const nodeMapping: RendererOptions["nodeMapping"] = {
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
  inlineMath: InlineMathNode,
  blockMath: BlockMathNode,
  footnoteReference: FootnoteReferenceNode,
  footnotes: FootnotesNode,
  footnoteItem: FootnoteItemNode,
};

function BoldMark({ children }: MarkComponentProps) {
  return h("strong", null, ...childList(children));
}

function ItalicMark({ children }: MarkComponentProps) {
  return h("em", null, ...childList(children));
}

function StrikeMark({ children }: MarkComponentProps) {
  return h("del", null, ...childList(children));
}

function CodeMark({ children }: MarkComponentProps) {
  return h("code", { className: "msg-content-md-code" }, ...childList(children));
}

function LinkMark({ mark, children }: MarkComponentProps) {
  return h(
    MarkdownLink,
    { href: mark.attrs?.href, title: mark.attrs?.title },
    ...childList(children),
  );
}

export const markMapping: RendererOptions["markMapping"] = {
  bold: BoldMark,
  italic: ItalicMark,
  strike: StrikeMark,
  code: CodeMark,
  link: LinkMark,
};

/** 把整理后的 Tiptap JSON 渲染为 React 元素。Renders normalized Tiptap JSON as React elements. */
export const renderMarkdownJson = TiptapStaticRenderer<React.ReactNode, JSONMarkType, JSONNodeType>(
  ({ component, props: { children, ...props } }) =>
    React.createElement(component as React.FC<typeof props>, props, ...childList(children)),
  { nodeMapping, markMapping },
);
