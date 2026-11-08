import type { JSONContent } from "@tiptap/core";

// MarkdownManager 为多余空行和文档首尾生成的空 paragraph 只出现在这些容器中；表格单元格中的 paragraph 由映射展开。
// MarkdownManager's empty paragraphs for extra blank lines and document edges appear only in these containers;
// paragraphs in table cells are unwrapped by the mapping.
const EMPTY_PARAGRAPH_CONTAINERS = new Set(["doc", "blockquote", "listItem", "footnoteItem"]);
const LINE_BREAK_WHITESPACE = /[ \t]*\n[ \t]*/g;

function normalizeText(text: JSONContent, trimLineBreaks: boolean): JSONContent {
  if (!trimLineBreaks || text.marks?.some((mark) => mark.type === "code")) return text;
  return { ...text, text: text.text?.replace(LINE_BREAK_WHITESPACE, "\n") };
}

function normalizeNode(node: JSONContent): JSONContent {
  if (!node.content) return node;
  // marked 保留续行的行首空白；codeBlock 的缩进保持原样。
  // marked keeps the leading whitespace of continuation lines; codeBlock indentation stays as is.
  const trimLineBreaks = node.type === "paragraph" || node.type === "heading";
  const content = node.content
    .map((child) =>
      child.type === "text" ? normalizeText(child, trimLineBreaks) : normalizeNode(child),
    )
    .filter((child) => child.type !== "text" || Boolean(child.text))
    .filter(
      (child) =>
        child.type !== "paragraph" ||
        Boolean(child.content?.length) ||
        !EMPTY_PARAGRAPH_CONTAINERS.has(node.type ?? ""),
    );
  return { ...node, content };
}

/**
 * 整理 parseMarkdown 的结果：脚注区域移到文档末尾，去掉续行空白、空文字与多余的空 paragraph。
 * Tidies parseMarkdown output: moves the footnotes section to the end and drops continuation whitespace,
 * empty text and stray empty paragraphs.
 */
export function normalizeMarkdownDocument(document: JSONContent): JSONContent {
  const content = document.content ?? [];
  const footnotes = content.filter((node) => node.type === "footnotes" && node.content?.length);
  const body = content.filter((node) => node.type !== "footnotes");
  return normalizeNode({ ...document, content: [...body, ...footnotes] });
}
