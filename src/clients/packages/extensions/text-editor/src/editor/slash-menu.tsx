import { useSyncExternalStore } from "react";
import { createPortal } from "react-dom";

import type { SlashMenuStore } from "./slash-command";

/**
 * `/` 命令菜单的列表，渲染在 SlashMenuStore 的元素中（suggestion 插件负责放置与定位）；没有匹配项时不显示。
 * The `/` command menu list, rendered into SlashMenuStore's element (the suggestion plugin mounts and positions it);
 * nothing shows without matches.
 */
export function SlashMenu({ store }: { store: SlashMenuStore }) {
  const state = useSyncExternalStore(store.subscribe, store.getSnapshot);
  if (state === null || state.items.length === 0) return null;
  return createPortal(
    <div className="slash-menu" role="listbox" aria-label="Commands">
      {state.items.map((item, index) => (
        <div
          key={item.title}
          role="option"
          aria-selected={index === state.selected}
          className="slash-menu-item"
          // 点击不移走编辑器的焦点与选区。Clicking keeps the editor's focus and selection.
          onMouseDown={(event) => event.preventDefault()}
          onClick={() => state.choose(item)}
        >
          {item.title}
        </div>
      ))}
    </div>,
    store.element,
  );
}
