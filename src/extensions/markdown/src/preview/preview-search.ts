import { Schema } from "@tiptap/pm/model";
import { EditorState } from "@tiptap/pm/state";
import type { SearchQuery } from "prosemirror-search";

import type { FindTarget } from "../shared/find-bar";
import {
  createSearchQuery,
  IDLE_RESULT,
  type FindQuery,
  type FindResult,
} from "../shared/find-query";
import { revealMatch } from "../shared/find-reveal";

// Preview 的搜索（Markdown 搜索替换设计第 6 节）：从 DOM 收集文字段，用与 Mixed 相同的 SearchQuery 查找，再用 CSS Custom
// Highlight API 高亮，React 渲染的 DOM 保持不变。
// Preview's find (Markdown find and replace design, section 6): text runs come from the DOM, the same SearchQuery as
// Mixed finds the matches, and the CSS Custom Highlight API paints them, leaving the DOM React renders unchanged.

/** Preview 渲染的块元素；原始 HTML 显示为文字，不会产生其他元素。The block elements Preview renders; raw HTML shows as
 *  text and adds no other elements. */
const BLOCKS = [
  "article",
  "blockquote",
  "div",
  "h1",
  "h2",
  "h3",
  "h4",
  "h5",
  "h6",
  "li",
  "ol",
  "p",
  "pre",
  "section",
  "table",
  "tbody",
  "td",
  "th",
  "thead",
  "tr",
  "ul",
].join(", ");

/** 不显示的文字：图表与图标、视觉隐藏的元素、已经渲染为图表的源码。Text that does not show. */
const EXCLUDED = 'script, style, svg, .sr-only, .mermaid-block[data-diagram="rendered"] > pre';

const MATCH_HIGHLIGHT = "markdown-find-match";
const CURRENT_HIGHLIGHT = "markdown-find-current";
const INVALID_RESULT: FindResult = { status: "invalid" };

/** 只有文字的文档：每个文字段是一个 block。A text-only document: one block per text run. */
const textSchema = new Schema({
  nodes: { doc: { content: "block+" }, block: { content: "text*" }, text: {} },
});

export type TextRun = { nodes: readonly Text[]; text: string };

/**
 * 按文档顺序收集 `root` 中显示的文字：块元素相同并且在遍历中相邻的文字节点组成一个文字段，行内元素不分隔文字段。
 * Collects the text `root` shows in document order: adjacent text nodes with the same block element form one run, so
 * inline elements do not split runs.
 */
export function collectTextRuns(root: HTMLElement): TextRun[] {
  const { NodeFilter, Node } = root.ownerDocument.defaultView!;
  const walker = root.ownerDocument.createTreeWalker(
    root,
    NodeFilter.SHOW_ELEMENT | NodeFilter.SHOW_TEXT,
    (node) => {
      if (node.nodeType === Node.TEXT_NODE) return NodeFilter.FILTER_ACCEPT;
      return (node as Element).matches(EXCLUDED)
        ? NodeFilter.FILTER_REJECT
        : NodeFilter.FILTER_SKIP;
    },
  );
  const runs: { nodes: Text[]; text: string }[] = [];
  let block: Element | null = null;
  for (let node = walker.nextNode(); node; node = walker.nextNode()) {
    const text = node as Text;
    if (text.data === "") continue;
    const owner = text.parentElement!.closest(BLOCKS);
    const last = runs.at(-1);
    if (last && owner === block) {
      last.nodes.push(text);
      last.text += text.data;
    } else {
      runs.push({ nodes: [text], text: text.data });
      block = owner;
    }
  }
  return runs;
}

export type TextMatch = { run: number; from: number; to: number };

/**
 * 用 SearchQuery 找出全部匹配，位置为文字段序号与段内偏移。prosemirror-search 按 textblock 查找，匹配不跨文字段。
 * Finds every match with the SearchQuery, as a run index and offsets in the run. prosemirror-search searches per
 * textblock, so no match crosses runs.
 */
export function findTextMatches(runs: readonly TextRun[], query: SearchQuery): TextMatch[] {
  if (runs.length === 0) return [];
  const doc = textSchema.node(
    "doc",
    null,
    runs.map((run) => textSchema.node("block", null, textSchema.text(run.text))),
  );
  const state = EditorState.create({ doc });
  const starts: number[] = [];
  doc.forEach((_block, offset) => starts.push(offset + 1));
  const matches: TextMatch[] = [];
  let run = 0;
  for (let pos = 0; ;) {
    const result = query.findNext(state, pos);
    if (!result) break;
    while (run + 1 < starts.length && starts[run + 1]! <= result.from) run += 1;
    matches.push({ run, from: result.from - starts[run]!, to: result.to - starts[run]! });
    pos = Math.max(result.to, pos + 1);
  }
  return matches;
}

/**
 * 文字段中 `from`～`to` 的 Range。起点在两个节点交界处时取后一个节点，终点在交界处时取前一个节点。
 * The Range of `from`–`to` in a run. A start on a node boundary takes the later node, an end on a boundary the earlier.
 */
export function matchRange(run: TextRun, from: number, to: number): Range {
  const range = run.nodes[0]!.ownerDocument.createRange();
  let offset = 0;
  let started = false;
  for (const node of run.nodes) {
    const end = offset + node.data.length;
    if (!started && from < end) {
      range.setStart(node, from - offset);
      started = true;
    }
    if (started && to <= end) {
      range.setEnd(node, to - offset);
      return range;
    }
    offset = end;
  }
  throw new Error(`The match ${from}–${to} is outside its text run.`);
}

function sameResult(a: FindResult, b: FindResult): boolean {
  if (a.status !== "matches" || b.status !== "matches") return a.status === b.status;
  return a.count === b.count && a.current === b.current;
}

export class PreviewSearch implements FindTarget {
  public readonly replace = null;
  private query: FindQuery | null = null;
  private searchQuery: SearchQuery | null = null;
  private ranges: Range[] = [];
  private current: number | null = null;
  private result: FindResult = IDLE_RESULT;
  /** 页面中有这里设置的高亮。Highlights set here are in the page. */
  private painted = false;
  private frame: number | null = null;
  private readonly listeners = new Set<() => void>();
  private readonly observer: MutationObserver;

  public constructor(private readonly root: HTMLElement) {
    // 图表渲染完成后 data-diagram 变化，源码随之隐藏。data-diagram changes once a diagram renders, hiding its source.
    this.observer = new MutationObserver(() => this.schedule());
    this.observer.observe(root, {
      childList: true,
      characterData: true,
      subtree: true,
      attributes: true,
      attributeFilter: ["data-diagram"],
    });
  }

  public readonly subscribe = (listener: () => void): (() => void) => {
    this.listeners.add(listener);
    return () => this.listeners.delete(listener);
  };

  public readonly getResult = (): FindResult => this.result;

  /**
   * 当前匹配为第一个底边在窗口顶部之下的匹配，没有时为第一个匹配。
   * The current match is the first one whose bottom is below the window top, or else the first match.
   */
  public setQuery(query: FindQuery | null): void {
    this.query = query;
    this.searchQuery = query === null ? null : createSearchQuery(query);
    this.collect();
    const index = this.ranges.findIndex((range) => range.getBoundingClientRect().bottom > 0);
    this.current = this.ranges.length === 0 ? null : Math.max(index, 0);
    this.update();
    this.reveal();
  }

  public findNext(): void {
    const count = this.ranges.length;
    if (count === 0) return;
    this.current = this.current === null ? 0 : (this.current + 1) % count;
    this.update();
    this.reveal();
  }

  public findPrevious(): void {
    const count = this.ranges.length;
    if (count === 0) return;
    this.current = this.current === null ? count - 1 : (this.current - 1 + count) % count;
    this.update();
    this.reveal();
  }

  public selectedText(): string | null {
    const selection = document.getSelection()!;
    if (selection.rangeCount === 0) return null;
    if (!this.root.contains(selection.getRangeAt(0).commonAncestorContainer)) return null;
    const text = selection.toString();
    return text === "" || text.includes("\n") ? null : text;
  }

  public focusContent(): void {
    // Preview 没有可以编辑的区域；搜索栏的输入框卸载后焦点回到页面。Preview has no editing area; focus returns to the
    // page once the find bar's inputs unmount.
  }

  public dispose(): void {
    this.observer.disconnect();
    if (this.frame !== null) cancelAnimationFrame(this.frame);
    this.listeners.clear();
    this.clearHighlights();
  }

  private collect(): void {
    const query = this.searchQuery;
    if (query === null || !query.valid) {
      this.ranges = [];
      return;
    }
    const runs = collectTextRuns(this.root);
    this.ranges = findTextMatches(runs, query).map(({ run, from, to }) =>
      matchRange(runs[run]!, from, to),
    );
  }

  /** DOM 变化后的下一个动画帧重新查找，保留仍然有效的当前序号，不滚动。Searches again in the next animation frame after
   *  a DOM change, keeping a current index that is still valid, without scrolling. */
  private schedule(): void {
    if (this.searchQuery === null || this.frame !== null) return;
    this.frame = requestAnimationFrame(() => {
      this.frame = null;
      if (this.searchQuery === null) return;
      this.collect();
      if (this.current !== null && this.current >= this.ranges.length) this.current = null;
      this.update();
    });
  }

  private update(): void {
    this.paint();
    const next = this.readResult();
    if (sameResult(next, this.result)) return;
    this.result = next;
    for (const listener of this.listeners) listener();
  }

  private readResult(): FindResult {
    if (this.query === null || this.query.search === "") return IDLE_RESULT;
    if (!this.searchQuery!.valid) return INVALID_RESULT;
    return { status: "matches", count: this.ranges.length, current: this.current };
  }

  /** 当前匹配不放进普通高亮，两种颜色不会叠加。The current match stays out of the plain highlight, so colors never mix. */
  private paint(): void {
    if (this.ranges.length === 0) {
      this.clearHighlights();
      return;
    }
    const others = this.ranges.filter((_range, index) => index !== this.current);
    CSS.highlights.set(MATCH_HIGHLIGHT, new Highlight(...others));
    if (this.current === null) CSS.highlights.delete(CURRENT_HIGHLIGHT);
    else CSS.highlights.set(CURRENT_HIGHLIGHT, new Highlight(this.ranges[this.current]!));
    this.painted = true;
  }

  private clearHighlights(): void {
    if (!this.painted) return;
    CSS.highlights.delete(MATCH_HIGHLIGHT);
    CSS.highlights.delete(CURRENT_HIGHLIGHT);
    this.painted = false;
  }

  /**
   * 匹配需要滚动时，先让横向滚动的祖先元素把它移到中部，再让窗口把它移到竖直中部。
   * When the match needs scrolling, horizontally scrolling ancestors move it to their middle first, then the window
   * moves it to its vertical middle.
   */
  private reveal(): void {
    if (this.current === null) return;
    const range = this.ranges[this.current]!;
    revealMatch(() => range.getBoundingClientRect(), range.startContainer.parentElement!, null);
  }
}
