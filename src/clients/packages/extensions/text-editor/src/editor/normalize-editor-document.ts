import type { JSONContent } from "@tiptap/core";

import { canonicalInlineContent, marksEqual } from "../markdown/markdown-inline";
import { escapeImageAlt } from "../markdown/markdown-syntax";
import {
  jsonDefinitions,
  resolveReference,
  type MarkdownDefinitions,
} from "./markdown-definitions";
import { parseEditorMarkdown } from "./markdown-parser";

// 文档规范化（设计 5.4），载入与内容对比时使用同一个函数。第 1 步的行内部分由 canonicalInlineContent 完成，
// 它同时统一 Markdown 没有写法的三种差别（hardBreak 上的标记等，见 markdown-inline.ts）。
// Document normalization (design section 5.4), the same function at load and for comparison. Step 1's inline part
// is canonicalInlineContent, which also unifies three differences Markdown cannot write (marks on hardBreak and so on,
// see markdown-inline.ts).

type MarkJson = { type: string; attrs?: Record<string, unknown> };

// 删除其中空 paragraph 与空原文块的节点。Nodes whose empty paragraphs and empty raw blocks are removed.
const DROPPED_BLOCK_CONTAINERS = new Set(["doc", "blockquote", "listItem"]);
const TEXT_BLOCKS = new Set(["paragraph", "heading"]);
// 内容为原文文字的块。Blocks whose content is literal text.
const LITERAL_BLOCKS = new Set(["codeBlock", "rawBlock", "frontMatter"]);
const LIST_TYPES = new Set(["bulletList", "orderedList"]);

function emptyParagraph(): JSONContent {
  return { type: "paragraph" };
}

function withMarks(node: JSONContent, marks: readonly MarkJson[]): JSONContent {
  const { marks: _previous, ...rest } = node;
  return marks.length ? { ...rest, marks: [...marks] } : rest;
}

function isEmptyParagraph(node: JSONContent): boolean {
  return node.type === "paragraph" && !node.content?.length;
}

// 内容为空的原文块与空 paragraph 一样被删除：两者写出后都没有文字。An empty raw block is removed like an empty
// paragraph: neither writes any text.
function isDroppedBlock(node: JSONContent): boolean {
  return isEmptyParagraph(node) || (node.type === "rawBlock" && !node.content?.length);
}

function linkMark(node: JSONContent): MarkJson | undefined {
  return node.marks?.find((mark) => mark.type === "link");
}

function sharedMarks(nodes: readonly JSONContent[]): MarkJson[] {
  const [first, ...rest] = nodes;
  return (first.marks ?? []).filter(
    (mark) =>
      mark.type !== "link" &&
      rest.every((node) => node.marks?.some((other) => marksEqual(mark, other))),
  );
}

/** 引用式链接或图片的标签部分：`[label]`、`[]` 或空。The label part of a reference link or image. */
function referenceSuffix(attrs: Record<string, unknown> | undefined): string {
  if (attrs?.referenceKind === "full") return `[${String(attrs.reference)}]`;
  return attrs?.referenceKind === "collapsed" ? "[]" : "";
}

// 第 5 步：没有定义的引用式链接按 marked 的结果改为文字。方括号带上包住整个链接的标记。
// Step 5: a reference link without a definition becomes text as marked parses it. The brackets carry the marks
// that enclose the whole link.
function unresolvedLinkText(
  run: JSONContent[],
  link: MarkJson,
  before: JSONContent | undefined,
  after: JSONContent | undefined,
): JSONContent[] {
  const enclosing = sharedMarks(run).filter(
    (mark) =>
      Boolean(before?.marks?.some((other) => marksEqual(mark, other))) ||
      Boolean(after?.marks?.some((other) => marksEqual(mark, other))),
  );
  const bracket = (text: string) => withMarks({ type: "text", text }, enclosing);
  const content = run.map((node) =>
    withMarks(node, node.marks?.filter((mark) => mark.type !== "link") ?? []),
  );
  return [bracket("["), ...content, bracket(`]${referenceSuffix(link.attrs)}`)];
}

// 第 5 步（图片）：定义存在时得到地址与标题。没有定义时 marked 把 `![说明][标签]` 中的 `!` 与 `[` 读成文字，再把后面的
// 说明当作行内 Markdown 解析，因此用 Edit 的解析读取图片自己的原文（带上文档中其他的定义，使说明中的引用与脚注引用照常
// 成立），得到的每个节点再带上图片的标记。
// Step 5 (images): with a definition the image gets its destination and title. Without one, marked reads the `!` and
// `[` of `![alt][label]` as text and parses the alt after them as inline Markdown, so the image's own source is read
// with Edit's parser (with the document's other definitions, so references and footnote references inside the alt
// resolve as usual), and every resulting node also carries the image's marks.
function normalizeReferenceImage(
  node: JSONContent,
  definitions: MarkdownDefinitions,
): JSONContent[] {
  const definition = resolveReference(definitions, String(node.attrs?.reference));
  if (definition) {
    return [{ ...node, attrs: { ...node.attrs, src: definition.href, title: definition.title } }];
  }
  const source = `![${escapeImageAlt(String(node.attrs?.alt ?? ""))}]${referenceSuffix(node.attrs)}`;
  const otherDefinitions = [
    ...[...definitions.links.keys()].map((label) => `[${label}]: /x`),
    ...[...definitions.footnotes].map((label) => `[^${label}]: x`),
  ];
  const [paragraph] = parseEditorMarkdown([source, ...otherDefinitions].join("\n\n")).content ?? [];
  if (paragraph?.type !== "paragraph") {
    throw new Error(
      `The alt text of a reference image does not read as inline Markdown: ${source}`,
    );
  }
  const marks = node.marks ?? [];
  return (paragraph.content ?? []).map((child) =>
    withMarks(child, [
      ...marks,
      ...(child.marks ?? []).filter((mark) => !marks.some((other) => marksEqual(mark, other))),
    ]),
  );
}

function normalizeInline(
  content: readonly JSONContent[],
  definitions: MarkdownDefinitions,
): JSONContent[] {
  const images = content.flatMap((node) =>
    node.type === "image" && node.attrs?.reference != null
      ? normalizeReferenceImage(node, definitions)
      : [node],
  );
  // 第 4 步：标签没有脚注定义的引用改为文字。Step 4: references whose label has no footnote definition become text.
  const nodes = images.map((node): JSONContent => {
    if (node.type !== "footnoteReference") return node;
    const label = String(node.attrs?.label ?? "");
    if (definitions.footnotes.has(label)) return node;
    return withMarks({ type: "text", text: `[^${label}]` }, node.marks ?? []);
  });
  const result: JSONContent[] = [];
  let index = 0;
  while (index < nodes.length) {
    const node = nodes[index];
    const link = linkMark(node);
    if (!link || link.attrs?.reference == null) {
      result.push(node);
      index++;
      continue;
    }
    let end = index;
    while (end + 1 < nodes.length && nodes[end + 1].marks?.some((mark) => marksEqual(mark, link))) {
      end++;
    }
    const run = nodes.slice(index, end + 1);
    const definition = resolveReference(definitions, String(link.attrs.reference));
    if (definition) {
      // 第 5 步：定义存在时得到地址与标题。Step 5: with a definition the link gets its destination and title.
      const resolved = { ...link, attrs: { ...link.attrs, ...definition } };
      for (const item of run) {
        result.push({
          ...item,
          marks: item.marks?.map((mark) =>
            mark === link || marksEqual(mark, link) ? resolved : mark,
          ),
        });
      }
    } else {
      result.push(...unresolvedLinkText(run, link, nodes[index - 1], nodes[end + 1]));
    }
    index = end + 1;
  }
  return canonicalInlineContent(result);
}

function normalizeChildren(node: JSONContent, definitions: MarkdownDefinitions): JSONContent[] {
  const children = (node.content ?? []).map((child) => normalizeNode(child, definitions));
  if (!DROPPED_BLOCK_CONTAINERS.has(node.type ?? "")) return children;
  return children.filter((child) => !isDroppedBlock(child));
}

// 第 6 步：任一列表项含有第二个及以后的 paragraph 或表格时 tight 为 false；只有一个列表项且它只有一个子块时，
// marked 总是把列表读为紧凑，tight 为 true。
// Step 6: tight is false when any item holds a second or later paragraph or a table; a list with a single item
// that has a single child always reads as tight in marked, so tight is true.
function listTightness(items: readonly JSONContent[], tight: unknown): boolean {
  const loose = items.some(
    (item) =>
      (item.content ?? []).filter((child) => child.type === "paragraph").length > 1 ||
      Boolean(item.content?.some((child) => child.type === "table")),
  );
  if (loose) return false;
  if (items.length === 1 && items[0].content?.length === 1) return true;
  return tight !== false;
}

function normalizeListItem(node: JSONContent, definitions: MarkdownDefinitions): JSONContent {
  const content = normalizeChildren(node, definitions);
  // 第 2 步：第一个子节点不是 paragraph 时在开头插入一个空 paragraph。Step 2: insert an empty paragraph when the
  // first child is not a paragraph.
  if (content[0]?.type !== "paragraph") content.unshift(emptyParagraph());
  const checked = node.attrs?.checked ?? null;
  // 第 3 步：第一个 paragraph 为空的任务项改为普通项，文字为 `[ ]` 或 `[x]`。Step 3: a task item with an empty first
  // paragraph becomes a plain item whose text is `[ ]` or `[x]`.
  if (checked !== null && isEmptyParagraph(content[0])) {
    content[0] = { type: "paragraph", content: [{ type: "text", text: checked ? "[x]" : "[ ]" }] };
    return { ...node, attrs: { ...node.attrs, checked: null }, content };
  }
  return { ...node, content };
}

function normalizeNode(node: JSONContent, definitions: MarkdownDefinitions): JSONContent {
  const type = node.type ?? "";
  if (TEXT_BLOCKS.has(type)) {
    const { content: _content, ...rest } = node;
    const content = normalizeInline(node.content ?? [], definitions);
    return content.length ? { ...rest, content } : rest;
  }
  if (type === "listItem") return normalizeListItem(node, definitions);
  if (LIST_TYPES.has(type)) {
    const content = normalizeChildren(node, definitions);
    return {
      ...node,
      attrs: { ...node.attrs, tight: listTightness(content, node.attrs?.tight) },
      content,
    };
  }
  if (LITERAL_BLOCKS.has(type)) {
    // 代码块、原文块与 front matter 的文字不变，只删除空文字节点。Text of code blocks, raw blocks and front matter
    // stays as is; only empty text nodes are dropped.
    const { content: _content, ...rest } = node;
    const content = node.content?.filter((child) => child.text);
    return content?.length ? { ...rest, content } : rest;
  }
  if (!node.content) return node;
  const content = normalizeChildren(node, definitions);
  // 第 2 步：没有子节点的 blockquote 保留一个空 paragraph。Step 2: an empty blockquote keeps one empty paragraph.
  if (type === "blockquote" && !content.length) content.push(emptyParagraph());
  return { ...node, content };
}

export type NormalizationContext = MarkdownDefinitions;

/** 文档中的定义，供逐块规范化使用。The document's definitions, for normalizing block by block. */
export function normalizationContext(doc: JSONContent): NormalizationContext {
  return jsonDefinitions(doc);
}

/**
 * 规范化一个顶层块；块为空 paragraph 或空原文块时返回 null（规范化会删除它）。
 * Normalizes one top-level block; returns null for an empty paragraph or an empty raw block, which normalization
 * removes.
 */
export function normalizeBlock(
  node: JSONContent,
  context: NormalizationContext,
): JSONContent | null {
  const normalized = normalizeNode(node, context);
  return isDroppedBlock(normalized) ? null : normalized;
}

/**
 * 规范化文档，并给出每个顶层子节点来自输入的哪个子节点（新增的空 paragraph 为 -1）。
 * Normalizes a document and reports which input child each top-level child comes from (-1 for an added empty
 * paragraph).
 */
export function normalizeDocumentWithOrigins(doc: JSONContent): {
  doc: JSONContent;
  origins: number[];
} {
  const context = normalizationContext(doc);
  const content: JSONContent[] = [];
  const origins: number[] = [];
  (doc.content ?? []).forEach((child, index) => {
    const normalized =
      child.type === "frontMatter" ? normalizeNode(child, context) : normalizeBlock(child, context);
    if (!normalized) return;
    content.push(normalized);
    origins.push(index);
  });
  // 第 2 步：没有块的文档保留一个空 paragraph。Step 2: a document without blocks keeps one empty paragraph.
  if (!content.some((child) => child.type !== "frontMatter")) {
    content.push(emptyParagraph());
    origins.push(-1);
  }
  return { doc: { ...doc, content }, origins };
}

/** 设计 5.4 的 normalizeEditorDocument。The normalizeEditorDocument of design section 5.4. */
export function normalizeEditorDocument(doc: JSONContent): JSONContent {
  return normalizeDocumentWithOrigins(doc).doc;
}
