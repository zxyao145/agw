import { Extension, type Editor } from "@tiptap/core";
import { Plugin, PluginKey, TextSelection, type Command, type EditorState } from "@tiptap/pm/state";
import {
  findNext,
  findPrev,
  getMatchHighlights,
  getSearchState,
  replaceAll,
  replaceNext,
  search,
  setSearchState,
  type SearchQuery,
  type SearchResult,
} from "prosemirror-search";

import type { FindTarget } from "../shared/find-bar";
import {
  createSearchQuery,
  EMPTY_QUERY,
  IDLE_RESULT,
  sameSearch,
  type FindQuery,
  type FindResult,
} from "../shared/find-query";
import { needsReveal } from "../shared/find-reveal";
import { inDiagramOnlyBlock, switchesMermaidView } from "./mermaid-editing";

// Mixed 的搜索与替换（Markdown 搜索替换设计第 5 节）：prosemirror-search 保存搜索条件并生成匹配装饰，当前匹配就是编辑器
// 选区。
// Mixed's find and replace (Markdown find and replace design, section 5): prosemirror-search keeps the search
// conditions and decorates the matches; the current match is the editor selection.

const ACTIVE_MATCH = ".ProseMirror-active-search-match";
const INVALID_RESULT: FindResult = { status: "invalid" };

/**
 * prosemirror-search 的插件，以及把当前匹配滚动到 `.edit-scroll` 中部的 `handleScrollToSelection`，选区不是匹配时交给
 * ProseMirror 的默认滚动。prosemirror-search 只在文档或选区变化时重新查找，切换 Mermaid 视图后由 `appendTransaction`
 * 再设置一次条件，"Diagram only" 视图的源码随之加入或离开匹配。
 * The prosemirror-search plugin, plus a `handleScrollToSelection` that scrolls the current match to the middle of
 * `.edit-scroll`, leaving a selection that is no match to ProseMirror's default scrolling. prosemirror-search searches
 * again only when the document or selection changes, so `appendTransaction` sets the conditions again after a Mermaid
 * view switch, and Diagram only source joins or leaves the matches.
 */
export const EditSearchExtension = Extension.create({
  name: "editSearch",
  // priority 低于默认的 100，插件排在 MermaidEditing 之后：查找匹配时新状态中的 Mermaid 视图已经更新。
  // A priority below the default 100 puts the plugins after MermaidEditing's, so the new state's Mermaid views are
  // already updated when matches are searched.
  priority: 50,
  addProseMirrorPlugins() {
    return [
      search(),
      new Plugin({
        key: new PluginKey("revealSearchMatch"),
        appendTransaction(transactions, _oldState, state) {
          if (!transactions.some(switchesMermaidView)) return null;
          const { query, range } = getSearchState(state)!;
          return query.valid ? setSearchState(state.tr, query, range) : null;
        },
        props: {
          handleScrollToSelection(view) {
            const match = view.dom.querySelector(ACTIVE_MATCH);
            if (!match) return false;
            const scroller = view.dom.closest(".edit-scroll");
            if (needsReveal(match.getBoundingClientRect(), match, scroller)) {
              match.scrollIntoView({ block: "center", inline: "nearest" });
            }
            return true;
          },
        },
      }),
    ];
  },
});

/** "Diagram only" 视图的 Mermaid 源码不显示，不参与搜索。Hidden Diagram only Mermaid source is left out. */
function excludeHidden(state: EditorState, result: SearchResult): boolean {
  return inDiagramOnlyBlock(state, result.from);
}

function readResult(state: EditorState): FindResult {
  const { query } = getSearchState(state)!;
  if (query.search === "") return IDLE_RESULT;
  if (!query.valid) return INVALID_RESULT;
  const matches = getMatchHighlights(state)
    .find()
    .sort((a, b) => a.from - b.from);
  const { from, to } = state.selection;
  const current = matches.findIndex((match) => match.from === from && match.to === to);
  return { status: "matches", count: matches.length, current: current < 0 ? null : current };
}

function sameResult(a: FindResult, b: FindResult): boolean {
  if (a.status !== "matches" || b.status !== "matches") return a.status === b.status;
  return a.count === b.count && a.current === b.current;
}

export class EditSearch implements FindTarget {
  public readonly replace = {
    next: () => this.run(replaceNext),
    all: () => this.run(replaceAll),
  };
  private query: FindQuery | null = null;
  private result: FindResult = IDLE_RESULT;
  private readonly listeners = new Set<() => void>();

  public constructor(private readonly editor: Editor) {
    editor.on("transaction", this.update);
  }

  public readonly subscribe = (listener: () => void): (() => void) => {
    this.listeners.add(listener);
    return () => this.listeners.delete(listener);
  };

  public readonly getResult = (): FindResult => this.result;

  /**
   * 搜索文字或选项变化时，同一个事务把选区设为从选区开头向后的第一个匹配（没有时从文档开头找）；只有替换文字变化时
   * 选区不变。
   * When the search text or options change, the same transaction selects the first match from the selection start on
   * (or from the document start); a change of the replace text alone keeps the selection.
   */
  public setQuery(query: FindQuery | null): void {
    const previous = this.query;
    this.query = query;
    const { state, view } = this.editor;
    const searchQuery = this.searchQuery();
    const tr = setSearchState(state.tr, searchQuery);
    if (
      query !== null &&
      searchQuery.valid &&
      (previous === null || !sameSearch(previous, query))
    ) {
      const { from } = state.selection;
      const match = searchQuery.findNext(state, from) ?? searchQuery.findNext(state, 0, from);
      if (match) {
        tr.setSelection(TextSelection.create(tr.doc, match.from, match.to)).scrollIntoView();
      }
    }
    view.dispatch(tr);
  }

  /**
   * 载入文档会用新的 EditorState 重新初始化插件，载入后用当前条件再设置一次，选区不变。
   * Loading a document reinitializes the plugins with a new EditorState; afterwards the current conditions are set
   * again, keeping the selection.
   */
  public restore(): void {
    const { state, view } = this.editor;
    view.dispatch(setSearchState(state.tr, this.searchQuery()));
  }

  public findNext(): void {
    this.run(findNext);
  }

  public findPrevious(): void {
    this.run(findPrev);
  }

  public selectedText(): string | null {
    const { selection, doc } = this.editor.state;
    if (!(selection instanceof TextSelection) || selection.empty) return null;
    if (!selection.$from.sameParent(selection.$to)) return null;
    const text = doc.textBetween(selection.from, selection.to);
    return text.includes("\n") ? null : text;
  }

  public focusContent(): void {
    this.editor.view.focus();
  }

  public dispose(): void {
    this.editor.off("transaction", this.update);
    this.listeners.clear();
  }

  private searchQuery(): SearchQuery {
    return createSearchQuery(this.query ?? EMPTY_QUERY, excludeHidden);
  }

  private run(command: Command): void {
    command(this.editor.state, this.editor.view.dispatch);
  }

  private readonly update = (): void => {
    const next = readResult(this.editor.state);
    if (sameResult(next, this.result)) return;
    this.result = next;
    for (const listener of this.listeners) listener();
  };
}
