import type { JSONContent } from "@tiptap/core";
import type { MarkdownManager } from "@tiptap/markdown";
import type { Node as ProseMirrorNode } from "@tiptap/pm/model";
import { diffArrays } from "diff";

import { listSymbolOf } from "../markdown/markdown-extensions";
import { createSpellingBudget, type SpellingBudget } from "../markdown/markdown-inline";
import { comparableDocument, findDifference } from "./document-equivalence";
import { documentDefinitions } from "./markdown-definitions";
import { loadMarkdown, type MarkdownBaseline } from "./markdown-loader";
import { createEditorRenderer } from "./markdown-parser";
import { normalizeBlock, type NormalizationContext } from "./normalize-editor-document";
import { MarkdownSourceMapError, type SourceBlock, type SourceRange } from "./source-map";

// 写回（设计 5.6）：顶层块对比，保留块取原文，修改块按 5.5 输出，顶层列表按列表项再对比；间隔与边界规则；
// 两项校验都通过才返回文字。
// Write-back (design section 5.6): compare top-level blocks, copy preserved blocks from the source, write modified
// blocks per section 5.5 and compare top-level lists item by item; apply the spacing and boundary rules; return the
// text only when both verifications pass.

export type RoundTripFailure =
  | {
      /** 写出的文本重新解析后内容不同。The written text parses to different content. */
      readonly kind: "content";
      readonly path: readonly number[];
      readonly expected: string;
      readonly actual: string;
    }
  | {
      /** 保留块或保留列表项的原文没有完整出现在输出中。A preserved block or item is not intact in the output. */
      readonly kind: "preserved";
      readonly block: number;
      readonly item: number | null;
    };

function describeFailure(failure: RoundTripFailure): string {
  if (failure.kind === "content") {
    return `The written Markdown parses to different content at block path [${failure.path.join(", ")}]: expected ${failure.expected}, found ${failure.actual}.`;
  }
  const item = failure.item === null ? "" : `, item ${failure.item}`;
  return `The source of preserved block ${failure.block}${item} is not intact in the written Markdown.`;
}

/**
 * 写出的文本不能还原编辑器中的文档时抛出。界面显示 "Could not save {name}: the document can't be written as Markdown
 * without changing it."，控制台记录 `failure` 中的块路径与类型。
 * Thrown when the written text cannot reproduce the editor's document. The UI shows "Could not save {name}: the
 * document can't be written as Markdown without changing it." and logs the block path and types in `failure`.
 */
export class MarkdownRoundTripError extends Error {
  readonly failure: RoundTripFailure;

  constructor(failure: RoundTripFailure, options?: ErrorOptions) {
    super(describeFailure(failure), options);
    this.name = "MarkdownRoundTripError";
    this.failure = failure;
  }
}

export type PreservedSpan = {
  /** 基线中的块序号；保留列表项时为所在列表的序号。The baseline block index; for a preserved item, its list's. */
  readonly block: number;
  readonly item: number | null;
  readonly output: SourceRange;
  readonly source: SourceRange;
};

export type MarkdownComposition = {
  readonly text: string;
  /** 输出中的保留块与保留列表项，按输出顺序。Preserved blocks and items in the output, in output order. */
  readonly preserved: readonly PreservedSpan[];
  /** 当前文档每个顶层子节点在输出中的区间。The output range of each top-level child of the current document. */
  readonly blocks: readonly SourceBlock[];
};

export type MarkdownWriteResult = {
  readonly text: string;
  /** 写入成功后的基线。The baseline after a successful write. */
  readonly baseline: MarkdownBaseline;
};

const LIST_TYPES = new Set(["bulletList", "orderedList"]);
const BLANK_LINE = /\n[ \t]*\n/;

type RenderedItem = { range: SourceRange; preserved: number | null };

type Output = {
  /** 当前文档中的序号。Index in the current document. */
  readonly index: number;
  readonly node: ProseMirrorNode;
  /** 基线中与左右两边对应的块，判断“在基线中相邻”。Baseline blocks matching the left and right edges, used to decide
   *  whether two outputs are adjacent in the baseline. */
  readonly left: number | null;
  readonly right: number | null;
  /** 保留块在基线中的序号。The baseline index of a preserved block. */
  preserved: number | null;
  /** 同一位置被替换的同类列表，按列表项对比。A same-kind list replaced at the same position, compared by items. */
  pairedList: number | null;
  text: string;
  items: RenderedItem[] | null;
  /** 输出的列表符号（无序列表的 marker 或有序列表的 delimiter）。The list symbol written (marker or delimiter). */
  symbol: string | null;
  indentedCode: boolean;
};

function children(node: ProseMirrorNode): ProseMirrorNode[] {
  const result: ProseMirrorNode[] = [];
  node.forEach((child) => result.push(child));
  return result;
}

// ProseMirror 编辑时共用未修改的子树，先判断是否为同一个节点对象，再用 Node.eq（包括写法属性）。
// ProseMirror shares unmodified subtrees while editing, so identity is checked before Node.eq (syntax attributes
// included).
function sameNode(a: ProseMirrorNode, b: ProseMirrorNode): boolean {
  return a === b || a.eq(b);
}

function listSymbol(node: {
  type?: { name: string } | string;
  attrs?: Record<string, unknown>;
}): string {
  const type = typeof node.type === "object" ? node.type.name : node.type;
  return String((type === "orderedList" ? node.attrs?.delimiter : node.attrs?.marker) ?? "");
}

function isList(output: Output): boolean {
  return LIST_TYPES.has(output.node.type.name);
}

function isFootnoteDefinition(output: Output): boolean {
  return output.node.type.name === "rawBlock" && output.node.attrs.kind === "footnote";
}

class Composer {
  private readonly source: string;
  private readonly blocks: readonly SourceBlock[];
  private readonly baselineChildren: ProseMirrorNode[];
  private readonly context: NormalizationContext;
  private readonly normalized = new Map<ProseMirrorNode, JSONContent | null>();

  constructor(
    private readonly baseline: MarkdownBaseline,
    private readonly doc: ProseMirrorNode,
    private readonly renderer: MarkdownManager,
  ) {
    this.source = baseline.source;
    this.blocks = baseline.blocks;
    this.baselineChildren = children(baseline.doc);
    this.context = documentDefinitions(doc);
  }

  compose(): MarkdownComposition {
    const outputs = this.diffBlocks();
    for (const output of outputs) this.render(output, {});
    const written = outputs.filter((output) => output.text);
    this.applyBoundaryRules(written);
    return this.assemble(outputs, written);
  }

  private normalizedJson(node: ProseMirrorNode): JSONContent | null {
    if (!this.normalized.has(node)) {
      this.normalized.set(node, normalizeBlock(node.toJSON() as JSONContent, this.context));
    }
    return this.normalized.get(node)!;
  }

  private diffBlocks(): Output[] {
    const current = children(this.doc);
    const changes = diffArrays(this.baselineChildren, current, { comparator: sameNode });
    const outputs: Output[] = [];
    let oldIndex = 0;
    let newIndex = 0;
    let removed: number[] = [];
    let added: number[] = [];
    const create = (
      index: number,
      preserved: number | null,
      left: number | null,
      right: number | null,
    ) =>
      ({
        index,
        node: current[index],
        left,
        right,
        preserved,
        pairedList: null,
        text: "",
        items: null,
        symbol: null,
        indentedCode: false,
      }) satisfies Output;
    const flush = () => {
      // 删除与插入的块数相同时按位置对应；否则第一个插入块对应第一个删除块的左边，最后一个对应最后一个的右边。
      // With equal counts removed and added blocks pair by position; otherwise the first added block takes the left
      // edge of the first removed block and the last added block the right edge of the last removed block.
      added.forEach((index, position) => {
        const paired = removed.length === added.length ? removed[position] : null;
        const output: Output = create(
          index,
          null,
          paired ?? (position === 0 && removed.length ? removed[0] : null),
          paired ?? (position === added.length - 1 && removed.length ? removed.at(-1)! : null),
        );
        if (
          paired !== null &&
          this.canDiffItems(this.baselineChildren[paired], current[index], paired)
        ) {
          output.pairedList = paired;
        }
        outputs.push(output);
      });
      removed = [];
      added = [];
    };
    for (const change of changes) {
      for (let count = 0; count < change.count; count++) {
        if (change.added) {
          added.push(newIndex++);
        } else if (change.removed) {
          removed.push(oldIndex++);
        } else {
          if (count === 0) flush();
          outputs.push(create(newIndex++, oldIndex, oldIndex, oldIndex));
          oldIndex++;
        }
      }
    }
    flush();
    return outputs;
  }

  // 同一位置一个块被删除、一个块被插入，两者是同类列表并且符号相同时，按列表项再对比一次。
  // When a block is removed and another inserted at the same position and both are same-kind lists with the same
  // symbol, the list is compared again item by item.
  private canDiffItems(old: ProseMirrorNode, next: ProseMirrorNode, index: number): boolean {
    return (
      LIST_TYPES.has(old.type.name) &&
      old.type === next.type &&
      listSymbol(old) === listSymbol(next) &&
      this.blocks[index].items !== null
    );
  }

  private render(output: Output, overrides: Record<string, unknown>): void {
    const { node } = output;
    output.items = null;
    output.symbol = null;
    if (output.preserved !== null) {
      const block = this.blocks[output.preserved];
      output.text = this.source.slice(block.range.from, block.range.to);
      output.items =
        block.items?.map((range, item) => ({
          range: { from: range.from - block.range.from, to: range.to - block.range.from },
          preserved: item,
        })) ?? null;
      if (isList(output)) output.symbol = this.sourceSymbol(output.preserved);
      output.indentedCode = node.type.name === "codeBlock" && node.attrs.fenced === false;
      return;
    }
    const json = this.normalizedJson(node);
    if (!json) {
      output.text = "";
      return;
    }
    const attrs = { ...json.attrs, ...overrides };
    if (LIST_TYPES.has(node.type.name)) {
      const paired = Object.keys(overrides).length ? null : output.pairedList;
      // 按列表项对比时，修改的列表项与保留的列表项一样使用基线列表原文中的符号。When compared item by item, modified
      // items use the symbol in the baseline list's source, like the preserved items.
      const symbol =
        paired === null
          ? {}
          : {
              [node.type.name === "orderedList" ? "delimiter" : "marker"]:
                this.sourceSymbol(paired),
            };
      const list = { ...json, attrs: { ...attrs, ...symbol } };
      output.symbol = listSymbol(list);
      Object.assign(output, this.renderList(list, node, paired));
      return;
    }
    output.text = this.renderer.renderNodes({ ...json, attrs }, { type: "doc" });
    output.indentedCode = node.type.name === "codeBlock" && output.text.startsWith("    ");
  }

  // 基线列表原文中的符号（列表的区间从第一项的原文开始）。写入后基线文档就是编辑器的文档：边界规则换过符号的列表，
  // 节点的 marker 或 delimiter 仍是原来的值，与写入的原文不同。
  // The symbol in the baseline list's source (a list's range starts with its first item's source). After a write the
  // baseline document is the editor's document: for a list whose symbol a boundary rule switched, the node's marker
  // or delimiter keeps its old value and differs from the written source.
  private sourceSymbol(index: number): string {
    const { range } = this.blocks[index];
    return listSymbolOf(
      this.baselineChildren[index].type.name === "orderedList",
      this.source.slice(range.from, range.to),
    );
  }

  // 基线列表原文中第一项前的缩进。The indentation before the first item in the baseline list's source.
  private sourceIndent(index: number): string {
    const { range } = this.blocks[index];
    return /^ */.exec(this.source.slice(range.from, range.to))![0];
  }

  private renderList(
    list: JSONContent,
    node: ProseMirrorNode,
    pairedList: number | null,
  ): { text: string; items: RenderedItem[] } {
    const items = list.content ?? [];
    const entries: { index: number; preserved: number | null }[] = [];
    const source = pairedList === null ? null : this.blocks[pairedList].items!;
    if (pairedList === null) {
      items.forEach((_item, index) => entries.push({ index, preserved: null }));
    } else {
      const changes = diffArrays(children(this.baselineChildren[pairedList]), children(node), {
        comparator: sameNode,
      });
      let oldIndex = 0;
      let newIndex = 0;
      for (const change of changes) {
        if (change.removed) {
          oldIndex += change.count;
        } else if (change.added) {
          for (let count = 0; count < change.count; count++) {
            entries.push({ index: newIndex++, preserved: null });
          }
        } else {
          for (let count = 0; count < change.count; count++) {
            entries.push({ index: newIndex++, preserved: oldIndex++ });
          }
        }
      }
      // marked 只用第一项的编号作为 start：保留的第一项编号不同时按修改项输出。
      // marked takes start from the first item's number only: a preserved first item with another number is written as
      // a modified item.
      const first = entries[0];
      if (list.type === "orderedList" && first?.preserved != null) {
        const range = source![first.preserved];
        const number = /^\s*(\d+)/.exec(this.source.slice(range.from, range.to))![1];
        if (Number(number) !== Number(list.attrs?.start ?? 1)) first.preserved = null;
      }
    }
    const tight = list.attrs?.tight !== false;
    // 按列表项对比时，修改的列表项每一行都加上基线列表原文中的缩进，与保留的列表项对齐；否则后面有缩进的保留项会被读成
    // 它的子块。新的列表没有缩进。
    // When compared item by item, every line of a modified item gets the indentation of the baseline list's source,
    // aligned with the preserved items; otherwise an indented preserved item after it would read as its child. A new
    // list has no indentation.
    const indent = pairedList === null ? "" : this.sourceIndent(pairedList);
    const texts = entries.map(({ index, preserved }) =>
      preserved !== null
        ? this.source.slice(source![preserved].from, source![preserved].to)
        : this.renderer
            .renderNodeToMarkdown(items[index], list, index)
            .split("\n")
            .map((line) => (line ? indent + line : line))
            .join("\n"),
    );
    const gaps = entries.slice(1).map((entry, position) => {
      const previous = entries[position];
      if (previous.preserved !== null && entry.preserved === previous.preserved + 1) {
        return this.source.slice(source![previous.preserved].to, source![entry.preserved].from);
      }
      return tight ? "\n" : "\n\n";
    });
    // 非紧凑列表至少保留一个空行，否则 marked 会把它读为紧凑列表。A loose list keeps at least one blank line between
    // items, or marked reads it as tight.
    if (!tight && gaps.length && !gaps.some((gap) => BLANK_LINE.test(gap))) gaps[0] = "\n\n";
    let text = "";
    const rendered: RenderedItem[] = [];
    texts.forEach((itemText, position) => {
      if (position > 0) text += gaps[position - 1];
      rendered.push({
        range: { from: text.length, to: text.length + itemText.length },
        preserved: entries[position].preserved,
      });
      text += itemText;
    });
    return { text, items: rendered };
  }

  private convert(output: Output, overrides: Record<string, unknown>): void {
    output.preserved = null;
    output.pairedList = null;
    this.render(output, overrides);
  }

  // 输出中相邻的两个块在基线中不相邻或至少一个是修改块时，依次检查三条边界规则。
  // When two neighboring outputs are not adjacent in the baseline or at least one is modified, the three boundary
  // rules are checked in order.
  private applyBoundaryRules(written: Output[]): void {
    for (let position = 1; position < written.length; position++) {
      const x = written[position - 1];
      const y = written[position];
      if (x.preserved !== null && y.preserved === x.preserved + 1) continue;
      // 1. 同类列表并且符号相同：Y 改用另一个符号，否则 marked 会把两个列表并成一个。
      // 1. Same-kind lists with the same symbol: Y switches symbol, or marked would merge the two lists.
      if (isList(x) && x.node.type === y.node.type && x.symbol === y.symbol) {
        const ordered = y.node.type.name === "orderedList";
        const other = ordered ? (x.symbol === "." ? ")" : ".") : x.symbol === "-" ? "*" : "-";
        this.convert(y, ordered ? { delimiter: other } : { marker: other });
      }
      // 2. 缩进的代码会被并入前面的列表项、脚注或缩进代码块：Y 用围栏输出。
      // 2. Indented code would join the preceding list item, footnote or indented code block: Y is fenced.
      if (y.indentedCode && (isList(x) || isFootnoteDefinition(x) || x.indentedCode)) {
        this.convert(y, { fenced: true });
      }
      // 3. 以空白开头的保留块（包括按列表项对比、带着原文缩进的列表）会被并入前面的列表项或脚注：Y 改为修改块。
      // 3. A preserved block starting with whitespace (a list compared item by item that keeps its source indentation
      // included) would join the preceding list item or footnote: Y is modified.
      if (
        (y.preserved !== null || y.pairedList !== null) &&
        /^[ \t]/.test(y.text) &&
        (isList(x) || isFootnoteDefinition(x))
      ) {
        this.convert(y, {});
      }
    }
  }

  private gap(x: Output, y: Output): string {
    if (x.preserved !== null && y.preserved === x.preserved + 1) {
      return this.source.slice(
        this.blocks[x.preserved].range.to,
        this.blocks[y.preserved].range.from,
      );
    }
    if (x.right !== null && y.left === x.right + 1) {
      // 原来的间隔含有空行时使用它，否则用一个空行，修改块的下一行不会被并入它。
      // 标题、分隔线、front matter 与围栏代码块在自己的最后一行结束，两个链接定义也互不并入，这时保留含有换行的原来的间隔
      // （没有原文的块，例如只有 front matter 的文件中规范化加入的 paragraph，间隔为空）。
      // Keep the original gap when it holds a blank line; otherwise one blank line, so the next line cannot join a
      // modified block. Headings, thematic breaks, front matter and fenced code end on their own last line, and two
      // link definitions never join, so those keep an original gap that holds a line ending (a block without source
      // text, such as the paragraph normalization adds to a file with only front matter, has an empty gap).
      const original = this.source.slice(
        this.blocks[x.right].range.to,
        this.blocks[y.left].range.from,
      );
      const kept =
        BLANK_LINE.test(original) || (original.includes("\n") && this.endsOnOwnLine(x, y));
      return kept ? original : "\n\n";
    }
    return "\n\n";
  }

  private endsOnOwnLine(x: Output, y: Output): boolean {
    const type = x.node.type.name;
    if (type === "heading" || type === "horizontalRule" || type === "frontMatter") return true;
    if (type === "codeBlock") return !x.indentedCode;
    const isDefinition = (output: Output) =>
      output.node.type.name === "rawBlock" && output.node.attrs.kind === "definition";
    return isDefinition(x) && isDefinition(y);
  }

  private assemble(outputs: Output[], written: Output[]): MarkdownComposition {
    // 基线末尾没有原文的块（编辑器在末尾补的空 paragraph 等）不算最后一个块。Blocks without source text at the end of
    // the baseline (such as empty paragraphs the editor appended) do not count as the last block.
    let last = this.blocks.length - 1;
    while (last > 0 && this.blocks[last].range.from === this.blocks[last].range.to) last--;
    const first = written[0];
    const final = written.at(-1);
    // 第一个块之前与最后一个块之后的原文在它们仍在原来的位置时保留。The text before the first block and after the
    // last block is kept while those blocks are still in place.
    let text = first?.left === 0 ? this.source.slice(0, this.blocks[0].range.from) : "";
    const preserved: PreservedSpan[] = [];
    const ranges = new Map<Output, SourceBlock>();
    let previous: Output | null = null;
    for (const output of outputs) {
      if (!output.text) {
        ranges.set(output, { range: { from: text.length, to: text.length }, items: null });
        continue;
      }
      if (previous) text += this.gap(previous, output);
      const from = text.length;
      text += output.text;
      const items =
        output.items?.map(({ range }) => ({ from: from + range.from, to: from + range.to })) ??
        null;
      ranges.set(output, { range: { from, to: text.length }, items });
      if (output.preserved !== null) {
        preserved.push({
          block: output.preserved,
          item: null,
          output: { from, to: text.length },
          source: this.blocks[output.preserved].range,
        });
      } else if (output.pairedList !== null) {
        output.items!.forEach(({ preserved: item }, position) => {
          if (item === null) return;
          preserved.push({
            block: output.pairedList!,
            item,
            output: items![position],
            source: this.blocks[output.pairedList!].items![item],
          });
        });
      }
      previous = output;
    }
    if (final && final.right !== null && final.right >= last) {
      text += this.source.slice(this.blocks[final.right].range.to);
    } else if (this.source.endsWith("\n")) {
      text += "\n";
    }
    return {
      text,
      preserved,
      blocks: outputs.map((output) => ranges.get(output)!),
    };
  }
}

/**
 * 组合输出（不校验）。`budget` 为本次保存的行内写法搜索，所有修改块共用：限制每段行内内容没有进展时的读回检查，某段
 * 找不到写法后其余内容不再搜索（见 markdown-inline.ts 的 SpellingBudget）。
 * Composes the output without verifying it. `budget` is this save's inline spelling search, shared by all modified
 * blocks: it limits the read-back checking of each inline content while it makes no progress, and once some content
 * finds no spelling the rest is not searched (see SpellingBudget in markdown-inline.ts).
 */
export function composeMarkdown(
  baseline: MarkdownBaseline,
  doc: ProseMirrorNode,
  budget: SpellingBudget = createSpellingBudget(),
): MarkdownComposition {
  return new Composer(baseline, doc, createEditorRenderer(budget)).compose();
}

function isLoadError(error: unknown): boolean {
  return error instanceof MarkdownSourceMapError || error instanceof RangeError;
}

/**
 * 两项校验：写出的文本按 5.1 的流程解析后与当前文档内容相同；每个保留块与保留列表项的原文完整出现在输出中，
 * 顺序与基线相同。任一项不通过时抛出 MarkdownRoundTripError；写出的 front matter 不是有效的 YAML 时抛出
 * FrontMatterError，界面显示它的信息 "Front matter isn't valid YAML: {message}"（设计 5.1）。
 * The two verifications: the written text, parsed per section 5.1, has the current document's content; every
 * preserved block and item appears intact in the output, in baseline order. Either failure throws
 * MarkdownRoundTripError; written front matter that is not valid YAML throws FrontMatterError, whose message "Front
 * matter isn't valid YAML: {message}" the UI shows (design section 5.1).
 */
export function verifyComposition(
  baseline: MarkdownBaseline,
  doc: ProseMirrorNode,
  composition: MarkdownComposition,
): void {
  let previous: PreservedSpan | null = null;
  for (const span of composition.preserved) {
    const intact =
      composition.text.slice(span.output.from, span.output.to) ===
      baseline.source.slice(span.source.from, span.source.to);
    const ordered =
      previous === null ||
      (span.output.from >= previous.output.to &&
        (span.block > previous.block ||
          (span.block === previous.block && (span.item ?? -1) > (previous.item ?? -1))));
    if (!intact || !ordered) {
      throw new MarkdownRoundTripError({ kind: "preserved", block: span.block, item: span.item });
    }
    previous = span;
  }
  let written: ProseMirrorNode;
  try {
    written = loadMarkdown(composition.text, doc.type.schema).doc;
  } catch (error) {
    if (!isLoadError(error)) throw error;
    throw new MarkdownRoundTripError(
      { kind: "content", path: [], expected: "a loadable document", actual: String(error) },
      { cause: error },
    );
  }
  const schema = doc.type.schema;
  const expected = comparableDocument(doc.toJSON() as JSONContent, schema);
  const actual = comparableDocument(written.toJSON() as JSONContent, schema);
  const difference = findDifference(expected, actual);
  if (difference) throw new MarkdownRoundTripError({ kind: "content", ...difference });
}

/**
 * 写回：当前文档与基线文档相同时返回 null（不写入）；否则返回写出的文本与写入成功后的基线。
 * Write-back: returns null (no write) when the current document equals the baseline document; otherwise the written
 * text and the baseline after a successful write.
 */
export function writeMarkdown(
  baseline: MarkdownBaseline,
  doc: ProseMirrorNode,
): MarkdownWriteResult | null {
  if (doc.eq(baseline.doc)) return null;
  const composition = composeMarkdown(baseline, doc, createSpellingBudget());
  verifyComposition(baseline, doc, composition);
  return {
    text: composition.text,
    baseline: { source: composition.text, doc, blocks: composition.blocks },
  };
}
