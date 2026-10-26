import { Extension, type Range } from "@tiptap/core";
import { PluginKey, type EditorState } from "@tiptap/pm/state";
import { Suggestion, type SuggestionProps } from "@tiptap/suggestion";

import type { EditUi } from "./edit-ui";
import { filterSlashItems, type SlashItem } from "./slash-items";

// `/` 命令菜单（设计 5.7）：在空 paragraph 中或空格之后输入 `/` 时由 `@tiptap/suggestion` 弹出，继续输入的文字用于过滤；
// `↑`、`↓` 选择，`Enter` 确认，`Esc` 关闭。菜单的状态放在 SlashMenuStore 中，React 组件按它渲染。
// The `/` command menu (design section 5.7): `@tiptap/suggestion` opens it when `/` is typed in an empty paragraph or
// after a space, and the text typed next filters it; `↑` and `↓` select, `Enter` confirms, `Esc` closes. The menu
// state lives in SlashMenuStore and a React component renders it.

export type SlashMenuState = {
  readonly items: readonly SlashItem[];
  readonly selected: number;
  choose(item: SlashItem): void;
};

export class SlashMenuStore {
  /** 菜单的元素，由 suggestion 插件放进页面并定位。The menu element, mounted and positioned by the suggestion
   *  plugin. */
  public readonly element = document.createElement("div");
  private state: SlashMenuState | null = null;
  private readonly listeners = new Set<() => void>();

  public readonly subscribe = (listener: () => void): (() => void) => {
    this.listeners.add(listener);
    return () => this.listeners.delete(listener);
  };

  public readonly getSnapshot = (): SlashMenuState | null => this.state;

  public show(props: SuggestionProps<SlashItem, SlashItem>): void {
    const sameItems =
      this.state !== null &&
      this.state.items.length === props.items.length &&
      this.state.items.every((item, index) => item === props.items[index]);
    this.set({
      items: props.items,
      selected: sameItems ? this.state!.selected : 0,
      choose: (item) => props.command(item),
    });
  }

  public hide(): void {
    this.set(null);
  }

  /** 选中第 `index` 项（鼠标移到该项上）。Selects item `index` (the mouse moved over it). */
  public select(index: number): void {
    const state = this.state;
    if (state === null || state.selected === index) return;
    this.set({ ...state, selected: index });
  }

  /** 菜单打开并有项目时处理方向键与 Enter。Handles the arrow keys and Enter while the menu is open with items. */
  public keyDown(event: KeyboardEvent): boolean {
    const state = this.state;
    if (state === null || state.items.length === 0) return false;
    const count = state.items.length;
    if (event.key === "ArrowDown" || event.key === "ArrowUp") {
      const step = event.key === "ArrowDown" ? 1 : count - 1;
      this.set({ ...state, selected: (state.selected + step) % count });
      return true;
    }
    if (event.key === "Enter") {
      state.choose(state.items[state.selected]);
      return true;
    }
    return false;
  }

  private set(state: SlashMenuState | null): void {
    this.state = state;
    for (const listener of this.listeners) listener();
  }
}

/**
 * `/` 只在空 paragraph 中或空格之后弹出菜单；代码块、rawBlock 与 frontMatter 中不弹出。`range` 为 `/` 与之后的过滤文字。
 * `/` opens the menu only in an empty paragraph or after a space, never in code blocks, rawBlock or frontMatter.
 * `range` covers the `/` and the filter text after it.
 */
function slashAllowed(state: EditorState, range: Range): boolean {
  const $slash = state.doc.resolve(range.from);
  const { parent } = $slash;
  if (parent.type.spec.code) return false;
  if ($slash.parentOffset === 0) {
    return parent.type.name === "paragraph" && parent.content.size === range.to - range.from;
  }
  return state.doc.textBetween(range.from - 1, range.from) === " ";
}

export function createSlashCommand(store: SlashMenuStore, ui: EditUi): Extension {
  return Extension.create({
    name: "slashCommand",
    addProseMirrorPlugins() {
      return [
        Suggestion<SlashItem, SlashItem>({
          editor: this.editor,
          pluginKey: new PluginKey("slashCommand"),
          char: "/",
          allow: ({ state, range }) => slashAllowed(state, range),
          items: ({ query, editor }) => filterSlashItems(query, editor.state),
          command: ({ editor, range, props }) => props.run(editor, range, ui),
          render: () => {
            let unmount: (() => void) | null = null;
            return {
              onStart: (props) => {
                unmount = props.mount(store.element);
                store.show(props);
              },
              onUpdate: (props) => store.show(props),
              onExit: () => {
                unmount?.();
                unmount = null;
                store.hide();
              },
              onKeyDown: ({ event }) => store.keyDown(event),
            };
          },
        }),
      ];
    },
  });
}
