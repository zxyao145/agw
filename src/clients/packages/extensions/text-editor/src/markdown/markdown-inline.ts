import type { JSONContent } from "@tiptap/core";
import type {
  Delete,
  Emphasis,
  FootnoteReference,
  Heading,
  Image,
  ImageReference,
  InlineCode,
  LinkReference,
  Paragraph,
  Parents,
  PhrasingContent,
  Strong,
  TableCell,
  Text,
} from "mdast";
import { gfmToMarkdown } from "mdast-util-gfm";
import {
  defaultHandlers,
  toMarkdown,
  type Handle,
  type Info,
  type Options,
  type State,
  type Unsafe,
} from "mdast-util-to-markdown";
import { classifyCharacter } from "micromark-util-classify-character";

import { escapeImageAlt, normalizeLinkLabel, startsWithDefinition } from "./markdown-syntax";

// Edit 输出行内内容（paragraph、标题与表格单元格中的文字、标记与行内节点）：Tiptap 的平铺标记先转为 mdast 的嵌套节点，
// 再由 mdast-util-to-markdown（带 GFM 扩展）输出。marked 与 CommonMark 不同的地方由它的 handlers 与 unsafe 选项处理；
// 分隔符的选择由 writeInline 用 Edit 的解析读回检查。
// Edit's output for inline content (text, marks and inline nodes of paragraphs, headings and table cells): Tiptap's
// flat marks become nested mdast nodes, which mdast-util-to-markdown (with the GFM extension) writes. Where marked
// differs from CommonMark, its handlers and unsafe options take over; writeInline chooses delimiters by reading the
// output back with Edit's parser.

// 行内 HTML 原文（rawInline 与 `<br>`）使用自定义的 rawHtml 节点：mdast-util-to-markdown 会把 mdast html 节点前的换行
// 改为空格，hardBreak 与 soft line break 都会因此改变。
// Raw inline HTML (rawInline and `<br>`) uses the custom rawHtml node: mdast-util-to-markdown turns a line ending
// before an mdast html node into a space, which would change hard breaks and soft line breaks.
type RawHtml = { type: "rawHtml"; value: string };

// 行内内容开头或结尾的一个空白字符，写为字符引用（见 encodeTrimmedEdges）。One whitespace character at the start or
// end of inline content, written as a character reference (see encodeTrimmedEdges).
type TrimmedSpace = { type: "trimmedSpace"; value: string };

// bold 与斜体的分隔符字符，`*` 或 `_`。The delimiter character of bold and italic, `*` or `_`.
type Marker = "*" | "_";

declare module "mdast" {
  interface PhrasingContentMap {
    rawHtml: RawHtml;
    trimmedSpace: TrimmedSpace;
  }
  interface RootContentMap {
    rawHtml: RawHtml;
    trimmedSpace: TrimmedSpace;
  }
  interface EmphasisData {
    marker?: Marker;
  }
  interface StrongData {
    marker?: Marker;
  }
  interface InlineCodeData {
    fence?: number;
  }
  interface DeleteData {
    marker?: "~~" | "~";
  }
}

type MarkJson = { type: string; attrs?: Record<string, unknown> };
type Container = Extract<PhrasingContent, { children: PhrasingContent[] }>;

// 嵌套顺序相同时的外层优先级：链接最外层。Outer-first order for ties: link outermost.
const MARK_RANK: Record<string, number> = { link: 0, bold: 1, italic: 2, strike: 3 };

function attrsEqual(
  a: Record<string, unknown> | undefined,
  b: Record<string, unknown> | undefined,
): boolean {
  const left = a ?? {};
  const right = b ?? {};
  const keys = new Set([...Object.keys(left), ...Object.keys(right)]);
  for (const key of keys) {
    if ((left[key] ?? null) !== (right[key] ?? null)) return false;
  }
  return true;
}

export function marksEqual(a: MarkJson, b: MarkJson): boolean {
  return a.type === b.type && attrsEqual(a.attrs, b.attrs);
}

function markSetsEqual(a: readonly MarkJson[] = [], b: readonly MarkJson[] = []): boolean {
  return a.length === b.length && a.every((mark) => b.some((other) => marksEqual(mark, other)));
}

function isCodeText(node: JSONContent): boolean {
  return node.type === "text" && Boolean(node.marks?.some((mark) => mark.type === "code"));
}

function withoutMark(node: JSONContent, type: string): JSONContent {
  const { marks: previous, ...rest } = node;
  const marks = previous?.filter((mark) => mark.type !== type) ?? [];
  return marks.length ? { ...rest, marks } : rest;
}

function trimLineEdges(nodes: JSONContent[]): void {
  // 前向去掉行首空白，后向去掉行尾空白；行由开头、hardBreak 与文字中的换行分开。
  // Leading whitespace forward, trailing whitespace backward; lines are delimited by the start, hardBreak nodes and
  // newlines inside text.
  let atLineStart = true;
  for (const node of nodes) {
    if (node.type === "hardBreak") {
      atLineStart = true;
    } else if (node.type === "text" && !isCodeText(node)) {
      let text = (node.text ?? "").replace(/\n[ \t]+/g, "\n");
      if (atLineStart) text = text.replace(/^[ \t]+/, "");
      node.text = text;
      if (text) atLineStart = text.endsWith("\n");
    } else {
      atLineStart = false;
    }
  }
  let atLineEnd = true;
  for (let index = nodes.length - 1; index >= 0; index--) {
    const node = nodes[index];
    if (node.type === "hardBreak") {
      atLineEnd = true;
    } else if (node.type === "text" && !isCodeText(node)) {
      let text = (node.text ?? "").replace(/[ \t]+\n/g, "\n");
      if (atLineEnd) text = text.replace(/[ \t]+$/, "");
      node.text = text;
      if (text) atLineEnd = text.startsWith("\n");
    } else {
      atLineEnd = false;
    }
  }
}

/**
 * 行内内容的规范形式（设计 5.4 第 1 步）：删除每一行开头与结尾的空格与制表符；hardBreak 没有标记，行内代码中的换行
 * 为空格，图片等行内节点没有行内代码标记（这三种差别在 Markdown 中没有写法，marked 也不会读出）；删除空文字节点，
 * 合并标记相同的相邻文字节点。
 * The canonical form of inline content (design section 5.4 step 1): spaces and tabs at the start and end of every
 * line are removed; hardBreak carries no marks, newlines inside inline code are spaces, and images and other inline
 * nodes carry no code mark (Markdown cannot write these differences and marked never reads them); empty text nodes
 * are dropped and adjacent text nodes with equal marks merge.
 */
export function canonicalInlineContent(content: readonly JSONContent[]): JSONContent[] {
  const nodes = content.map((node): JSONContent => {
    if (node.type === "hardBreak") return { type: "hardBreak" };
    if (isCodeText(node)) return { ...node, text: (node.text ?? "").replaceAll("\n", " ") };
    if (node.type !== "text") return withoutMark(node, "code");
    if (node.marks && !node.marks.length) return withoutMark(node, "");
    return { ...node };
  });
  trimLineEdges(nodes);
  const merged: JSONContent[] = [];
  for (const node of nodes) {
    if (node.type === "text" && !node.text) continue;
    const previous = merged.at(-1);
    if (
      previous?.type === "text" &&
      node.type === "text" &&
      markSetsEqual(previous.marks, node.marks)
    ) {
      merged[merged.length - 1] = { ...previous, text: (previous.text ?? "") + node.text };
    } else {
      merged.push(node);
    }
  }
  return merged;
}

// 行内代码是 mdast 的叶子节点，其余标记成为包住内容的节点。Inline code is an mdast leaf; the other marks become
// nodes around the content.
function containerMarks(node: JSONContent): MarkJson[] {
  if (node.type === "hardBreak") return [];
  return (node.marks ?? []).filter((mark) => mark.type !== "code");
}

function runEnds(nodes: readonly JSONContent[]): Map<MarkJson, number>[] {
  // 每个节点上每个标记所在连续段的结束位置。The end index of the run each mark of each node belongs to.
  const ends = nodes.map(() => new Map<MarkJson, number>());
  for (let index = nodes.length - 1; index >= 0; index--) {
    for (const mark of containerMarks(nodes[index])) {
      const next =
        nodes[index + 1] &&
        containerMarks(nodes[index + 1]).find((other) => marksEqual(other, mark));
      ends[index].set(mark, next ? ends[index + 1].get(next)! : index);
    }
  }
  return ends;
}

function container(mark: MarkJson): Container {
  const attrs = mark.attrs ?? {};
  switch (mark.type) {
    case "bold":
      return { type: "strong", children: [] };
    case "italic":
      return { type: "emphasis", children: [] };
    case "strike":
      return { type: "delete", children: [] };
    case "link": {
      const reference = attrs.reference as string | null | undefined;
      if (reference == null) {
        return {
          type: "link",
          url: String(attrs.href ?? ""),
          title: (attrs.title as string | null | undefined) ?? null,
          children: [],
        };
      }
      return {
        type: "linkReference",
        identifier: normalizeLinkLabel(reference),
        label: reference,
        referenceType: attrs.referenceKind as LinkReference["referenceType"],
        children: [],
      };
    }
    default:
      throw new Error(`Cannot write the mark "${mark.type}" as Markdown.`);
  }
}

// `\` 加换行只在前后都是同一 paragraph 中的内容时表示换行；开头、结尾、连续的 hardBreak 与换行旁边用 `<br>`。
// A backslash line break works only between content on both lines; at the start, the end, next to another hardBreak
// or a newline it is `<br>`.
function breakAsTag(nodes: readonly JSONContent[], index: number): boolean {
  const previous = nodes[index - 1];
  const next = nodes[index + 1];
  return (
    previous === undefined ||
    next === undefined ||
    previous.type === "hardBreak" ||
    next.type === "hardBreak" ||
    Boolean(previous.type === "text" && previous.text?.endsWith("\n")) ||
    Boolean(next.type === "text" && next.text?.startsWith("\n"))
  );
}

function leaf(nodes: readonly JSONContent[], index: number, singleLine: boolean): PhrasingContent {
  const node = nodes[index];
  const attrs = node.attrs ?? {};
  switch (node.type) {
    case "text":
      return isCodeText(node)
        ? { type: "inlineCode", value: node.text ?? "" }
        : { type: "text", value: node.text ?? "" };
    case "hardBreak":
      // 标题与表格单元格只占一行，hardBreak 输出为 `<br>`。Headings and table cells take one line, so hardBreak
      // renders as `<br>`.
      return singleLine || breakAsTag(nodes, index)
        ? { type: "rawHtml", value: "<br>" }
        : { type: "break" };
    case "image": {
      const reference = attrs.reference as string | null | undefined;
      if (reference != null) {
        return {
          type: "imageReference",
          identifier: normalizeLinkLabel(reference),
          label: reference,
          referenceType: attrs.referenceKind as ImageReference["referenceType"],
          alt: String(attrs.alt ?? ""),
        };
      }
      return {
        type: "image",
        url: String(attrs.src ?? ""),
        alt: String(attrs.alt ?? ""),
        title: (attrs.title as string | null | undefined) ?? null,
      };
    }
    case "rawInline":
      return { type: "rawHtml", value: String(attrs.raw ?? "") };
    case "footnoteReference": {
      const label = String(attrs.label ?? "");
      return { type: "footnoteReference", identifier: normalizeLinkLabel(label), label };
    }
    default:
      throw new Error(`Cannot write the inline node "${node.type}" as Markdown.`);
  }
}

/**
 * 平铺的标记转为嵌套的 mdast 节点：标记在打开时按连续段的结束位置排列，结束得晚的在外层；要关闭外层时内层先关闭，
 * 仍然有效的内层标记随后重新打开。
 * Flat marks become nested mdast nodes: marks opening together are ordered by where their runs end, the later one
 * outside; closing an outer mark closes the inner ones first, and inner marks still in effect reopen afterwards.
 */
function toPhrasing(content: readonly JSONContent[], singleLine: boolean): PhrasingContent[] {
  const ends = runEnds(content);
  const root: PhrasingContent[] = [];
  const stack: { mark: MarkJson; children: PhrasingContent[] }[] = [];
  content.forEach((node, index) => {
    const marks = containerMarks(node);
    let keep = 0;
    while (keep < stack.length && marks.some((mark) => marksEqual(mark, stack[keep].mark))) keep++;
    stack.length = keep;
    const toOpen = marks
      .filter((mark) => !stack.some((entry) => marksEqual(entry.mark, mark)))
      .sort(
        (a, b) =>
          ends[index].get(b)! - ends[index].get(a)! || MARK_RANK[a.type] - MARK_RANK[b.type],
      );
    for (const mark of toOpen) {
      const node = container(mark);
      (stack.at(-1)?.children ?? root).push(node);
      stack.push({ mark, children: node.children });
    }
    (stack.at(-1)?.children ?? root).push(leaf(content, index, singleLine));
  });
  return root;
}

// JavaScript 的 \s 中除空格、制表符与换行以外的字符（U+00A0、U+3000、U+2000–U+200A、U+FEFF 等）。U+000B 也除外：它的
// 字符引用解码为 U+FFFD，没有写法。
// The characters of JavaScript's \s other than spaces, tabs and line endings (U+00A0, U+3000, U+2000–U+200A, U+FEFF
// and so on). U+000B is left out too: its character reference decodes to U+FFFD, so nothing can write it.
const TRIMMED_SPACE = /^[^\S \t\n\r\v]$/;

/**
 * marked 用 JavaScript 的 trim() 去掉标题与表格单元格两端的空白，也去掉最后一个列表项结尾的空白，在解码字符引用之前；
 * canonicalInlineContent 只删除空格与制表符。开头（`leading` 为 true 时）与结尾的这种字符写为字符引用，marked 读回
 * 同一个字符。标记中的这种字符由分隔符的规则写为字符引用，这里只处理根层级的文字。
 * marked removes whitespace with JavaScript's trim() at both ends of headings and table cells and at the end of the
 * last list item, before it decodes character references, while canonicalInlineContent removes only spaces and tabs.
 * Such a character at the start (when `leading` is true) and at the end is written as a character reference, which
 * marked reads back as the same character. Inside marks the delimiter rules already write it as a character
 * reference, so only root-level text is handled here.
 */
function encodeTrimmedEdges(children: PhrasingContent[], leading: boolean): PhrasingContent[] {
  const result = [...children];
  const first = result[0];
  if (leading && first?.type === "text" && TRIMMED_SPACE.test(first.value.charAt(0))) {
    const rest: PhrasingContent[] =
      first.value.length > 1 ? [{ type: "text", value: first.value.slice(1) }] : [];
    result.splice(0, 1, { type: "trimmedSpace", value: first.value.charAt(0) }, ...rest);
  }
  const last = result.at(-1);
  if (last?.type === "text" && TRIMMED_SPACE.test(last.value.charAt(last.value.length - 1))) {
    const rest: PhrasingContent[] =
      last.value.length > 1 ? [{ type: "text", value: last.value.slice(0, -1) }] : [];
    result.splice(-1, 1, ...rest, { type: "trimmedSpace", value: last.value.slice(-1) });
  }
  return result;
}

// 引用式链接与图片（设计 5.5）：collapsed 与 shortcut 写法用链接文字或图片说明作为标签，只有输出后的文字按 marked 的
// 规则等于标签时才能使用；shortcut 后面紧跟 `[`（另一个链接或脚注引用）或 `:` 时会被读成其他写法，改用 collapsed
// （后面文字中的 `(` 由 mdast-util-to-markdown 转义）。标签按原样输出。
// Reference links and images (design section 5.5): the collapsed and shortcut forms use the link text or image alt
// as the label, so they apply only when the written text equals the label by marked's rule; a shortcut followed by
// `[` (another link or a footnote reference) or `:` would read as another construct, so it becomes collapsed
// (mdast-util-to-markdown escapes a `(` in the following text). The label is written as it was.
function referenceSuffix(text: string, node: LinkReference | ImageReference, info: Info): string {
  const label = node.label ?? node.identifier;
  if (normalizeLinkLabel(text) === normalizeLinkLabel(label)) {
    const next = info.after.charAt(0);
    if (node.referenceType === "shortcut" && next !== "[" && next !== ":") return "";
    if (node.referenceType !== "full") return "[]";
  }
  return `[${label}]`;
}

function linkReference(
  node: LinkReference,
  _parent: Parents | undefined,
  state: State,
  info: Info,
): string {
  const exit = state.enter("linkReference");
  const subexit = state.enter("label");
  const tracker = state.createTracker(info);
  const text = state.containerPhrasing(node, {
    before: tracker.move("["),
    after: "]",
    ...tracker.current(),
  });
  subexit();
  exit();
  return `[${text}]${referenceSuffix(text, node, info)}`;
}
linkReference.peek = () => "[";

// 图片的地址与标题由 mdast-util-to-markdown 输出，说明文字按 marked 的规则输出。An image's destination and title are
// written by mdast-util-to-markdown, its alt text by marked's rule.
function image(node: Image, parent: Parents | undefined, state: State, info: Info): string {
  const value = defaultHandlers.image({ ...node, alt: "" }, parent, state, info);
  return `![${escapeImageAlt(node.alt ?? "")}${value.slice("![".length)}`;
}
image.peek = () => "!";

function imageReference(
  node: ImageReference,
  _parent: Parents | undefined,
  _state: State,
  info: Info,
): string {
  const alt = escapeImageAlt(node.alt ?? "");
  return `![${alt}]${referenceSuffix(alt, node, info)}`;
}
imageReference.peek = () => "!";

// 脚注引用的标签按原样输出，marked-footnote 按原样对比标签。A footnote label is written as it was; marked-footnote
// compares labels as written.
function footnoteReference(node: FootnoteReference): string {
  return `[^${node.label ?? node.identifier}]`;
}
footnoteReference.peek = () => "[";

// 以行内 HTML 标签或 `<br>` 开始的行可能被读作 HTML 块；行首加一个写为字符引用的空格，它在规范形式中被删除。
// A line starting with an inline HTML tag or `<br>` may read as an HTML block; the line gets a leading space written
// as a character reference, which the canonical form removes.
function rawHtml(node: RawHtml, _parent: Parents | undefined, _state: State, info: Info): string {
  return (/^$|[\r\n]$/.test(info.before) ? "&#x20;" : "") + node.value;
}
rawHtml.peek = () => "<";

// marked 的两处差别：链接文字在解析之前去掉 `[`、`]` 前的反斜杠，行内代码中的也一样；结尾的反斜杠紧跟反引号时，
// marked 寻找强调与链接范围时把它当作转义，加空格隔开。表格单元格中的 `|` 转义（与 GFM 扩展相同）。
// Two marked differences: link text loses the backslash before `[` or `]` before it is parsed, inside inline code
// too; a trailing backslash right before the closing backtick reads as an escape when marked looks for emphasis and
// link ranges, so a space separates them. `|` is escaped inside table cells (as the GFM extension does). data.fence
// sets the fence length (see writeInline).
function inlineCode(node: InlineCode, parent: Parents | undefined, state: State): string {
  const output = defaultHandlers.inlineCode(node, parent, state);
  const shortest = /^`+/.exec(output)![0];
  const fence = node.data?.fence ? "`".repeat(node.data.fence) : shortest;
  let inner = output.slice(shortest.length, output.length - shortest.length);
  if (state.stack.includes("label")) inner = inner.replace(/\\(?=[[\]])/g, "\\\\");
  if (inner.endsWith("\\")) inner = ` ${inner} `;
  if (state.stack.includes("tableCell")) inner = inner.replace(/\|/g, "\\|");
  return fence + inner + fence;
}
inlineCode.peek = () => "`";

function characterReference(code: number): string {
  return `&#x${code.toString(16).toUpperCase()};`;
}

// peek 返回 `&`，前面文字结尾的反斜杠由 mdast-util-to-markdown 转义，不会转义字符引用。peek returns `&`, so
// mdast-util-to-markdown escapes a backslash ending the preceding text, which then cannot escape the reference.
function trimmedSpace(node: TrimmedSpace): string {
  return characterReference(node.value.charCodeAt(0));
}
trimmedSpace.peek = () => "&";

// 分隔符内外两个字符需要怎样写为字符引用，规则与 mdast-util-to-markdown 的强调相同（`~~` 与 `*` 一样允许在词中）。
// Which of the two characters around a delimiter must become character references; the rule is the one
// mdast-util-to-markdown uses for emphasis (`~~`, like `*`, may sit inside a word).
function encodeInfo(outside: number, inside: number): { inside: boolean; outside: boolean } {
  const outsideKind = classifyCharacter(outside);
  const insideKind = classifyCharacter(inside);
  if (insideKind === 1) return { inside: true, outside: outsideKind !== 2 };
  if (outsideKind === undefined && insideKind === 2) return { inside: false, outside: true };
  return { inside: false, outside: false };
}

// GFM 扩展的删除线分隔符不检查两边的字符；这里按强调的规则把挡住分隔符的字符写为字符引用。分隔符为 data.marker
// （`~~` 或 `~`，见 writeInline），默认 `~~`。
// The GFM extension writes strikethrough delimiters without checking the characters around them; this handler writes
// the characters that would block a delimiter as character references, by the emphasis rule. The delimiter is
// data.marker (`~~` or `~`, see writeInline), `~~` by default.
function strikethrough(
  node: Delete,
  _parent: Parents | undefined,
  state: State,
  info: Info,
): string {
  const marker = node.data?.marker ?? "~~";
  // 与 GFM 扩展相同的构造名。The same construct name as the GFM extension.
  const exit = state.enter("strikethrough" as Parameters<State["enter"]>[0]);
  const tracker = state.createTracker(info);
  const before = tracker.move(marker);
  let between = tracker.move(
    state.containerPhrasing(node, { ...tracker.current(), before, after: "~" }),
  );
  const open = encodeInfo(info.before.charCodeAt(info.before.length - 1), between.charCodeAt(0));
  if (open.inside) between = characterReference(between.charCodeAt(0)) + between.slice(1);
  const close = encodeInfo(info.after.charCodeAt(0), between.charCodeAt(between.length - 1));
  if (close.inside) {
    between = between.slice(0, -1) + characterReference(between.charCodeAt(between.length - 1));
  }
  exit();
  state.attentionEncodeSurroundingInfo = { after: close.outside, before: open.outside };
  return before + between + tracker.move(marker);
}
strikethrough.peek = () => "~";

// bold 与斜体的分隔符字符由节点的 data.marker 决定（没有时斜体用 `_`、bold 用 `**`），选择见 writeInline。
// The delimiter character of bold and italic comes from the node's data.marker (by default `_` for italic and `**` for
// bold); writeInline chooses it.
function attentionMarker(node: Emphasis | Strong): Marker {
  return node.data?.marker ?? (node.type === "emphasis" ? "_" : "*");
}

function emphasis(node: Emphasis, parent: Parents | undefined, state: State, info: Info): string {
  const previous = state.options.emphasis;
  state.options.emphasis = attentionMarker(node);
  try {
    return defaultHandlers.emphasis(node, parent, state, info);
  } finally {
    state.options.emphasis = previous;
  }
}
emphasis.peek = attentionMarker;

function strong(node: Strong, parent: Parents | undefined, state: State, info: Info): string {
  const previous = state.options.strong;
  state.options.strong = attentionMarker(node);
  try {
    return defaultHandlers.strong(node, parent, state, info);
  } finally {
    state.options.strong = previous;
  }
}
strong.peek = attentionMarker;

// 以链接或脚注引用开始、可能被读作定义的 paragraph，开头加一个写为字符引用的空格。A paragraph starting with a link or
// footnote reference that could read as a definition gets a leading space written as a character reference.
function paragraph(node: Paragraph, parent: Parents | undefined, state: State, info: Info): string {
  const value = defaultHandlers.paragraph(node, parent, state, info);
  return startsWithDefinition(value) ? `&#x20;${value}` : value;
}

// paragraph 不能以空行开始、结束或含有空行：开头、结尾与紧跟另一个换行的换行写为字符引用。（换行不能写成 unsafe 规则：
// 换行在 unsafe 中时，hardBreak 的默认输出变为空格。）
// A paragraph cannot start or end with an empty line or contain one: a newline at the start, at the end or right
// after another newline is written as a character reference. (Newlines cannot be unsafe rules: with a newline in
// unsafe, the default hardBreak output becomes a space.)
// 强调分隔符旁边的字符可能被 mdast-util-to-markdown 写为字符引用；它前面是文字中的反斜杠时，反斜杠会转义字符引用的 `&`，
// 因此结尾的“反斜杠加一个非标点字符”中的反斜杠转义。
// mdast-util-to-markdown may write the character next to an emphasis delimiter as a character reference; a literal
// backslash before it would then escape the reference's `&`, so the backslash of a trailing "backslash plus a
// non-punctuation character" is escaped.
function text(node: Text, parent: Parents | undefined, state: State, info: Info): string {
  let value = defaultHandlers
    .text(node, parent, state, info)
    .replace(/\n(\n+)/g, (_run, rest: string) => `\n${"&#xA;".repeat(rest.length)}`);
  const ending = node.value.slice(-2);
  if (/^\\[^!-/:-@[-`{-~]$/.test(ending) && value.endsWith(ending)) {
    value = `${value.slice(0, -2)}\\${ending}`;
  }
  if (value.startsWith("\n") && /^$|[\r\n]$/.test(info.before)) value = `&#xA;${value.slice(1)}`;
  if (value.endsWith("\n") && /^[\r\n]/.test(info.after)) value = `${value.slice(0, -1)}&#xA;`;
  return value;
}

// marked 跳过 `<` 与 `>` 之间的文字寻找强调的结束分隔符（即使它不是 HTML 标签），文字中的 `<` 都转义。
// marked skips text between `<` and `>` when it looks for an emphasis closing delimiter (even when it is not an HTML
// tag), so every `<` in text is escaped.
const UNSAFE: Unsafe[] = [{ character: "<", inConstruct: "phrasing" }];

const OPTIONS: Options = {
  extensions: [gfmToMarkdown()],
  handlers: {
    delete: strikethrough as Handle,
    emphasis: emphasis as Handle,
    footnoteReference: footnoteReference as Handle,
    image: image as Handle,
    imageReference: imageReference as Handle,
    inlineCode: inlineCode as Handle,
    linkReference: linkReference as Handle,
    paragraph: paragraph as Handle,
    rawHtml: rawHtml as Handle,
    strong: strong as Handle,
    text: text as Handle,
    trimmedSpace: trimmedSpace as Handle,
  },
  unsafe: UNSAFE,
};
const SETEXT_OPTIONS: Options = { ...OPTIONS, setext: true };

type InlineBlock = Paragraph | Heading | TableCell;

/** 用 Edit 的配置把 Markdown 正文解析为 Tiptap JSON。Parses a Markdown body into Tiptap JSON with Edit's configuration. */
export type MarkdownReader = (markdown: string) => JSONContent;

let markdownReader: MarkdownReader | null = null;

/**
 * 登记 Edit 的解析函数（src/editor/markdown-parser.ts 载入时调用），writeInline 用它检查写出的文字，没有登记时抛出。
 * Registers Edit's parser (src/editor/markdown-parser.ts calls it when it loads); writeInline checks its output with
 * it and throws without it.
 */
export function registerMarkdownReader(reader: MarkdownReader): void {
  markdownReader = reader;
}

function serialize(node: InlineBlock, options: Options): string {
  const output = toMarkdown(node, options);
  return output.endsWith("\n") ? output.slice(0, -1) : output;
}

/**
 * 写法可以选择的节点：bold 与斜体有两种分隔符字符（下标 0 为默认）；含有反引号的行内代码可以用三种不在内容中出现的
 * 反引号串长度作为分隔符（下标 0 为 mdast-util-to-markdown 选择的最短长度）；包含这种行内代码的删除线可以用 `~~` 或
 * `~`（marked 只配对长度相同的 `~` 串）。`from` 与 `to` 为节点内容在行内内容中的位置（单位与 matchingPrefix 相同）。
 * Nodes whose spelling can be chosen: bold and italic have two delimiter characters (option 0 is the default); inline
 * code holding a backtick can use three fence lengths that do not occur in its content (option 0 is the shortest,
 * which mdast-util-to-markdown chooses); strikethrough around such inline code can use `~~` or `~` (marked pairs only
 * `~` runs of equal length). `from` and `to` locate the node's content in the inline content (in matchingPrefix's
 * units).
 */
type SpellingChoice = {
  readonly options: number;
  readonly apply: (option: number) => void;
  readonly from: number;
  readonly to: number;
};

function fenceLengths(value: string): number[] {
  const runs = new Set((value.match(/`+/g) ?? []).map((run) => run.length));
  const lengths: number[] = [];
  for (let length = 1; lengths.length < 3; length++) if (!runs.has(length)) lengths.push(length);
  return lengths;
}

function holdsBacktickCode(node: PhrasingContent): boolean {
  if (node.type === "inlineCode") return node.value.includes("`");
  return "children" in node && node.children.some(holdsBacktickCode);
}

function spellingChoice(node: PhrasingContent): Omit<SpellingChoice, "from" | "to"> | null {
  if (node.type === "emphasis" || node.type === "strong") {
    const markers: Marker[] = node.type === "emphasis" ? ["_", "*"] : ["*", "_"];
    return { options: 2, apply: (option) => (node.data = { marker: markers[option] }) };
  }
  if (node.type === "delete" && holdsBacktickCode(node)) {
    const markers = ["~~", "~"] as const;
    return { options: 2, apply: (option) => (node.data = { marker: markers[option] }) };
  }
  if (node.type === "inlineCode" && node.value.includes("`")) {
    const lengths = fenceLengths(node.value);
    return { options: 3, apply: (option) => (node.data = { fence: lengths[option] }) };
  }
  return null;
}

// 叶子节点在行内内容中占的长度：文字与行内代码按字符，其余行内节点为 1（与 canonicalInlineContent 的节点一一对应）。
// The length a leaf takes in the inline content: text and inline code count characters, other inline nodes count 1
// (leaves correspond one to one with canonicalInlineContent's nodes).
function leafLength(node: PhrasingContent): number {
  return node.type === "text" || node.type === "inlineCode" ? node.value.length : 1;
}

function spellingChoices(block: InlineBlock): SpellingChoice[] {
  const choices: { -readonly [Key in keyof SpellingChoice]: SpellingChoice[Key] }[] = [];
  let offset = 0;
  const visit = (node: PhrasingContent) => {
    const choice = spellingChoice(node);
    const entry = choice && { ...choice, from: offset, to: offset };
    if (entry) choices.push(entry);
    if ("children" in node) node.children.forEach(visit);
    else offset += leafLength(node);
    if (entry) entry.to = offset;
  };
  block.children.forEach(visit);
  return choices;
}

// 在第一处不同附近尝试的节点数上限：按与该处的距离由近到远逐个加入。The most nodes tried around the first
// difference, added one at a time from the nearest.
const LOCAL_LIMIT = 6;

// 全部组合不超过这个数量时，附近的节点都不能推进后再尝试全部节点。When all combinations number at most this many, all
// nodes are tried after the nearby ones make no progress.
const EXHAUSTIVE_COMBINATIONS = 1024;

// 一段行内内容在第一处不同没有后移时最多用于读回检查的量，按字符计：每次检查计写出的字符数加 READ_BACK_COST（解析的
// 固定耗时约等于这么多字符）。约等于 150 ms 的解析。
// The most read-back checking one inline content may spend while its first difference does not move, counted in
// characters: each check costs the written characters plus READ_BACK_COST (the fixed cost of a parse is about that
// many characters). It is about 150 ms of parsing.
const STALL_LIMIT = 150_000;
const READ_BACK_COST = 200;

/**
 * 一次保存（writeMarkdown）中行内写法搜索的限制与状态，由调用方创建并传给每次行内输出。`limit` 为每段行内内容在读回的
 * 第一处不同没有后移时最多用于读回检查的量（单位见 STALL_LIMIT），Infinity 为不限；默认写法的检查不计入，搜索成功的
 * 内容不影响其他内容。某段行内内容找不到能读回的写法后 `stopped` 为 true：保存必然在校验时失败，其余行内内容不再搜索，
 * 直接用默认写法。
 * The limit and state of the inline spelling search in one save (writeMarkdown); the caller creates it and passes it
 * to every inline output. `limit` is the most read-back checking one inline content may spend while the first
 * difference of its read-back does not move (in STALL_LIMIT's units), Infinity for no limit; checking the default
 * spelling is free, and a successful search never affects other content. Once some inline content finds no spelling
 * that reads back, `stopped` is true: the save will fail verification anyway, so the remaining inline content is not
 * searched and keeps the default spelling.
 */
export type SpellingBudget = { readonly limit: number; stopped: boolean };

export function createSpellingBudget(limit: number = STALL_LIMIT): SpellingBudget {
  return { limit, stopped: false };
}

/**
 * 以 `current` 为基础、只改变 `indices` 中节点的组合，改变的节点少的在前，同样多时先改变 `indices` 中靠前的节点（不含
 * `current` 本身）。
 * Combinations based on `current` that change only the nodes in `indices`, fewest changes first and, for equal
 * counts, nodes earlier in `indices` first (`current` itself excluded).
 */
function* assignmentsWithin(
  current: readonly number[],
  options: readonly number[],
  indices: readonly number[],
): Generator<number[]> {
  function* change(size: number, start: number, assignment: number[]): Generator<number[]> {
    if (size === 0) {
      yield assignment;
      return;
    }
    for (let position = start; position < indices.length; position++) {
      const index = indices[position];
      for (let option = 0; option < options[index]; option++) {
        if (option !== current[index]) {
          yield* change(size - 1, position + 1, assignment.with(index, option));
        }
      }
    }
  }
  for (let size = 1; size <= indices.length; size++) yield* change(size, 0, [...current]);
}

function distance(choice: SpellingChoice, at: number): number {
  if (choice.from <= at && at <= choice.to) return 0;
  return Math.min(Math.abs(choice.from - at), Math.abs(choice.to - at));
}

/**
 * 第一处不同位于 `at` 时依次尝试的节点集合：离它最近的 1 到 LOCAL_LIMIT 个节点（包含它的节点距离为 0）；节点更多但全部
 * 组合不超过 EXHAUSTIVE_COMBINATIONS 时，最后是全部节点。
 * The node sets tried when the first difference is at `at`: the nearest 1 to LOCAL_LIMIT nodes (a node containing it
 * is at distance 0); with more nodes whose combinations number at most EXHAUSTIVE_COMBINATIONS, finally all nodes.
 */
function* searchWindows(choices: readonly SpellingChoice[], at: number): Generator<number[]> {
  const position = Math.max(at, 0);
  const order = choices
    .map((_choice, index) => index)
    .sort((a, b) => distance(choices[a], position) - distance(choices[b], position) || a - b);
  for (let size = 1; size <= Math.min(LOCAL_LIMIT, order.length); size++) {
    yield order.slice(0, size);
  }
  const combinations = choices.reduce((product, choice) => product * choice.options, 1);
  if (order.length > LOCAL_LIMIT && combinations <= EXHAUSTIVE_COMBINATIONS) yield order;
}

/**
 * 局部逐步搜索写法：从默认写法开始，在读回内容的第一处不同附近改变节点的写法（searchWindows，每个集合中改变的节点少的
 * 在前），采用第一个使第一处不同后移或消失的组合，再从新的位置继续，每一步都用 Edit 的解析读回。第一处不同没有后移时
 * 用于读回检查的量超过 `limit`，或某处的所有节点集合都不能推进时返回 null。
 * Local, incremental spelling search: starting from the default spelling, change the spelling of nodes around the
 * first difference of the read-back (searchWindows, fewest changes first within each set), adopt the first
 * combination that moves the first difference later or removes it, and continue from the new position; every step is
 * read back with Edit's parser. Returns null when the checking spent without the first difference moving exceeds
 * `limit`, or when no node set makes progress at some position.
 */
function searchSpelling(
  write: () => string,
  choices: readonly SpellingChoice[],
  reached: (markdown: string) => number,
  start: number,
  limit: number,
): string | null {
  const options = choices.map((choice) => choice.options);
  let current = choices.map(() => 0);
  let at = start;
  const tried = new Set([current.join()]);
  let spent = 0;
  for (;;) {
    let next: number[] | null = null;
    windows: for (const indices of searchWindows(choices, at)) {
      for (const assignment of assignmentsWithin(current, options, indices)) {
        const key = assignment.join();
        if (tried.has(key)) continue;
        tried.add(key);
        choices.forEach((choice, index) => choice.apply(assignment[index]));
        const markdown = write();
        spent += READ_BACK_COST + markdown.length;
        if (spent > limit) return null;
        const prefix = reached(markdown);
        if (prefix === MATCH) return markdown;
        if (prefix > at) {
          next = assignment;
          at = prefix;
          break windows;
        }
      }
    }
    if (!next) return null;
    current = next;
    spent = 0;
  }
}

function referenceKey(reference: unknown): string {
  return `ref:${normalizeLinkLabel(String(reference))}`;
}

function markKey(mark: MarkJson): string {
  if (mark.type !== "link") return mark.type;
  const attrs = mark.attrs ?? {};
  const target =
    attrs.reference != null
      ? referenceKey(attrs.reference)
      : JSON.stringify([attrs.href ?? null, attrs.title ?? null]);
  return `link:${target}`;
}

/** 对比用的行内节点：`key` 为类型、标记与决定含义的属性，文字节点的文字在 `text` 中。An inline node for comparison:
 *  `key` holds the type, marks and the attributes that carry meaning; a text node's text is in `text`. */
type ComparableNode = { readonly key: string; readonly text: string | null };

/**
 * 对比用的行内内容：引用式链接与图片只看标签（地址来自定义），其他属性取决定含义的部分。
 * Inline content for comparison: reference links and images compare by label only (the destination comes from the
 * definition), and other nodes by the attributes that carry meaning.
 */
function comparableInline(content: readonly JSONContent[]): ComparableNode[] {
  return canonicalInlineContent(content).map((node) => {
    const attrs = node.attrs ?? {};
    const marks = (node.marks ?? []).map(markKey).sort();
    switch (node.type) {
      case "text":
        return { key: JSON.stringify(["text", marks]), text: node.text ?? "" };
      case "image": {
        const target =
          attrs.reference != null
            ? referenceKey(attrs.reference)
            : [attrs.src ?? null, attrs.title ?? null];
        return { key: JSON.stringify(["image", target, attrs.alt || "", marks]), text: null };
      }
      case "rawInline":
        return { key: JSON.stringify(["rawInline", attrs.raw, marks]), text: null };
      case "footnoteReference":
        return { key: JSON.stringify(["footnoteReference", attrs.label, marks]), text: null };
      default:
        return { key: JSON.stringify([node.type, marks]), text: null };
    }
  });
}

// 读回的内容与期望内容相同。The read-back content equals the expected content.
const MATCH = Number.POSITIVE_INFINITY;

function commonPrefixLength(a: string, b: string): number {
  let length = 0;
  while (length < a.length && length < b.length && a[length] === b[length]) length++;
  return length;
}

/**
 * 读回的内容与期望内容相同的开头长度（文字按字符、其余行内节点按 1 计），即第一处不同的位置；完全相同时为 MATCH。
 * The length of the read-back content's start that equals the expected content (text counts characters, other
 * inline nodes count 1), which is where the first difference is; MATCH when they are equal.
 */
function matchingPrefix(
  expected: readonly ComparableNode[],
  actual: readonly ComparableNode[],
): number {
  let length = 0;
  for (const [index, node] of expected.entries()) {
    const other = actual[index];
    if (!other || other.key !== node.key) return length;
    if (other.text !== node.text) {
      return length + commonPrefixLength(node.text ?? "", other.text ?? "");
    }
    length += node.text?.length ?? 1;
  }
  return actual.length === expected.length ? MATCH : length;
}

/** 让引用式链接、图片与脚注引用在单独解析时成立的定义。Definitions that let reference links, images and footnote
 *  references resolve when the block is parsed alone. */
function definitionsFor(content: readonly JSONContent[]): string {
  const lines = new Set<string>();
  for (const node of content) {
    if (node.type === "footnoteReference") lines.add(`[^${String(node.attrs?.label)}]: x`);
    const reference =
      node.type === "image"
        ? node.attrs?.reference
        : node.marks?.find((mark) => mark.type === "link")?.attrs?.reference;
    if (reference != null) lines.add(`[${String(reference)}]: /x`);
  }
  return [...lines].join("\n\n");
}

function readInlineBack(
  reader: MarkdownReader,
  block: InlineBlock,
  markdown: string,
  definitions: string,
): JSONContent[] | null {
  const source = block.type === "tableCell" ? `| ${markdown} |\n| --- |` : markdown;
  const doc = reader(`${source}\n\n${definitions}`);
  const type = block.type === "tableCell" ? "table" : block.type;
  const node = doc.content?.find((child) => child.type === type);
  if (!node) return null;
  const inline = block.type === "tableCell" ? node.content?.[0]?.content?.[0]?.content?.[0] : node;
  return inline?.content ?? [];
}

/**
 * 输出行内内容，并选择 bold 与斜体的分隔符字符（`*` 或 `_`）以及含有反引号的行内代码的分隔符长度（spellingChoices）。
 * marked 判断分隔符的规则与 CommonMark 有多处不同（GFM 规则对 `*` 不把 `~` 当作标点、按三的倍数规则跳过两边都可能的
 * 分隔符、寻找强调范围时只跳过不含反引号的行内代码并按长度相同的反引号串配对等），因此先用默认写法输出，用 Edit 的解析
 * 读回检查；内容不同时用 searchSpelling 从第一处不同开始逐步改变附近节点的写法。找不到时使用默认写法，保存在校验时按
 * 设计失败，本次保存的其余行内内容不再搜索（`budget.stopped`）。
 * Writes inline content and chooses the delimiter character (`*` or `_`) of bold and italic and the fence length of
 * inline code holding a backtick (spellingChoices). marked classifies delimiters differently from CommonMark in
 * several ways (its GFM rules do not count `~` as punctuation for `*`, it skips delimiters that could open or close by
 * the multiple-of-three rule, and when it looks for emphasis ranges it skips only backtick-free inline code, pairing
 * backtick runs of equal length, and so on), so the default spelling is written first and read back with Edit's
 * parser; when the content differs, searchSpelling changes the spelling of nearby nodes step by step from the first
 * difference. When nothing is found, the default spelling stays, saving fails verification as designed, and the rest
 * of the save's inline content is not searched (`budget.stopped`).
 */
function writeInline(
  block: InlineBlock,
  options: Options,
  content: readonly JSONContent[],
  budget: SpellingBudget,
): string {
  const reader = markdownReader;
  if (!reader) {
    throw new Error(
      "Inline Markdown is written only after src/editor/markdown-parser.ts registers Edit's parser.",
    );
  }
  const written = serialize(block, options);
  const choices = spellingChoices(block);
  if (!choices.length || budget.stopped) return written;
  const expected = comparableInline(content);
  const definitions = definitionsFor(content);
  // 读回为另一种块时为 -1，任何能读回同一种块的写法都更好。-1 when the read-back is another kind of block, so
  // any spelling that reads back the same kind is better.
  const reached = (markdown: string) => {
    const actual = readInlineBack(reader, block, markdown, definitions);
    return actual === null ? -1 : matchingPrefix(expected, comparableInline(actual));
  };
  const start = reached(written);
  if (start === MATCH) return written;
  const found = searchSpelling(
    () => serialize(block, options),
    choices,
    reached,
    start,
    budget.limit,
  );
  if (found !== null) return found;
  budget.stopped = true;
  return written;
}

/** paragraph 的内容。The content of a paragraph. */
export function renderParagraphMarkdown(
  content: readonly JSONContent[],
  budget: SpellingBudget,
): string {
  return writeInline(
    { type: "paragraph", children: encodeTrimmedEdges(toPhrasing(content, false), false) },
    OPTIONS,
    content,
    budget,
  );
}

/** 标题；`setext` 为 true 时 1、2 级的单行标题用 setext 写法。A heading; with `setext`, single-line level 1 and 2
 *  headings use the setext form. */
export function renderHeadingMarkdown(
  level: number,
  setext: boolean,
  content: readonly JSONContent[],
  budget: SpellingBudget,
): string {
  return writeInline(
    {
      type: "heading",
      depth: level as Heading["depth"],
      children: encodeTrimmedEdges(toPhrasing(content, true), true),
    },
    setext ? SETEXT_OPTIONS : OPTIONS,
    content,
    budget,
  );
}

/** 表格单元格的内容（不含两边的 `|`）。The content of a table cell (without the surrounding `|`). */
export function renderTableCellMarkdown(
  content: readonly JSONContent[],
  budget: SpellingBudget,
): string {
  return writeInline(
    { type: "tableCell", children: encodeTrimmedEdges(toPhrasing(content, true), true) },
    OPTIONS,
    content,
    budget,
  );
}
