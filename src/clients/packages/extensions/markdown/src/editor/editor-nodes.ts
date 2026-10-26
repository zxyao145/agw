import {
  Extension,
  Node,
  type JSONContent,
  type MarkdownParseHelpers,
  type MarkdownToken,
} from "@tiptap/core";

import { recordBlock } from "../markdown/markdown-blocks";
import { withoutTrailingNewlines } from "../markdown/markdown-syntax";

// 编辑器专用的节点（设计 5.2）。界面任务用 `.extend({ addNodeView })` 为它们增加 node view。
// Editor-only nodes (design section 5.2). The UI task adds node views with `.extend({ addNodeView })`.

export type RawBlockKind = "html" | "definition" | "footnote";

function nodeText(node: JSONContent): string {
  return (node.content ?? []).map((child) => child.text ?? "").join("");
}

/** doc 的内容为 `frontMatter? block+`。The doc's content is `frontMatter? block+`. */
export const EditorDocument = Node.create({
  name: "doc",
  topNode: true,
  content: "frontMatter? block+",
});

/** 文件开头的 YAML front matter；文字为两行 `---` 之间的内容。The YAML front matter at the start of the file; its
 *  text is what lies between the two `---` lines. */
export const FrontMatter = Node.create({
  name: "frontMatter",
  content: "text*",
  marks: "",
  code: true,
  defining: true,
  parseHTML: () => [{ tag: "pre[data-front-matter]", preserveWhitespace: "full" }],
  renderHTML: () => ["pre", { "data-front-matter": "" }, ["code", 0]],
  renderMarkdown: (node: JSONContent) => `---\n${nodeText(node)}\n---`,
});

/** HTML 块、链接定义与脚注定义的原文。The source text of an HTML block, a link definition or a footnote definition. */
export const RawBlock = Node.create({
  name: "rawBlock",
  group: "block",
  content: "text*",
  marks: "",
  code: true,
  defining: true,
  addAttributes: () => ({ kind: { default: "html", rendered: false } }),
  parseHTML: () => [
    {
      tag: "pre[data-raw-block]",
      preserveWhitespace: "full",
      getAttrs: (element) => ({ kind: (element as HTMLElement).getAttribute("data-raw-block") }),
    },
  ],
  renderHTML: ({ node }) => ["pre", { "data-raw-block": node.attrs.kind }, ["code", 0]],
  markdownTokenName: "html",
  parseMarkdown: (token: MarkdownToken, helpers: MarkdownParseHelpers) =>
    rawBlockNode("html", token, helpers),
  renderMarkdown: (node: JSONContent) => nodeText(node),
});

function rawBlockNode(kind: RawBlockKind, token: MarkdownToken, helpers: MarkdownParseHelpers) {
  const text = withoutTrailingNewlines(token.raw ?? "");
  return recordBlock(
    helpers.createNode("rawBlock", { kind }, text ? [helpers.createTextNode(text)] : []),
  );
}

// 每个链接定义（包括重复的定义）与脚注定义都留在原来的位置。Every link definition (duplicates included) and footnote
// definition stays where it is.
export const DefinitionBlock = Extension.create({
  name: "definitionBlock",
  markdownTokenName: "definition",
  parseMarkdown: (token: MarkdownToken, helpers: MarkdownParseHelpers) =>
    rawBlockNode("definition", token, helpers),
});

export const FootnoteDefinitionBlock = Extension.create({
  name: "footnoteDefinitionBlock",
  markdownTokenName: "footnote",
  parseMarkdown: (token: MarkdownToken, helpers: MarkdownParseHelpers) =>
    rawBlockNode("footnote", token, helpers),
});

/** 行内 HTML 标签的原文。The source text of an inline HTML tag. */
export const RawInline = Node.create({
  name: "rawInline",
  group: "inline",
  inline: true,
  atom: true,
  addAttributes: () => ({ raw: { default: "" } }),
  parseHTML: () => [
    {
      tag: "code[data-raw-inline]",
      getAttrs: (element) => ({ raw: (element as HTMLElement).textContent ?? "" }),
    },
  ],
  renderHTML: ({ node }) => ["code", { "data-raw-inline": "" }, node.attrs.raw],
  markdownTokenName: "rawInline",
  parseMarkdown: (token: MarkdownToken, helpers: MarkdownParseHelpers) =>
    helpers.createNode("rawInline", { raw: token.raw }),
  renderMarkdown: (node: JSONContent) => String(node.attrs?.raw ?? ""),
});

/** 脚注引用 `[^label]`。A footnote reference `[^label]`. */
export const FootnoteReference = Node.create({
  name: "footnoteReference",
  group: "inline",
  inline: true,
  atom: true,
  addAttributes: () => ({ label: { default: "" } }),
  parseHTML: () => [
    {
      tag: "sup[data-footnote-reference]",
      getAttrs: (element) => ({
        label: (element as HTMLElement).getAttribute("data-footnote-reference"),
      }),
    },
  ],
  renderHTML: ({ node }) => [
    "sup",
    { "data-footnote-reference": node.attrs.label },
    `[${node.attrs.label}]`,
  ],
  markdownTokenName: "footnoteRef",
  parseMarkdown: (token: MarkdownToken, helpers: MarkdownParseHelpers) =>
    helpers.createNode("footnoteReference", { label: token.label }),
  renderMarkdown: (node: JSONContent) => `[^${String(node.attrs?.label ?? "")}]`,
});
