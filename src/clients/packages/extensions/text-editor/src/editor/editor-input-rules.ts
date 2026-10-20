import { Extension, InputRule } from "@tiptap/core";

import { markdownDefinitionsKey } from "./markdown-definitions";

// 输入规则（设计 5.7）：列表项开头输入 `[ ] ` 或 `[x] ` 时转换为任务项；输入 `[^label]` 并且文档中已有同名的脚注定义块
// 时转换为脚注引用。
// Input rules (design section 5.7): typing `[ ] ` or `[x] ` at the start of a list item makes it a task item; typing
// `[^label]` becomes a footnote reference when the document already has a footnote definition with that label.

const TASK_PATTERN = /^\[([ xX])\] $/;
const FOOTNOTE_PATTERN = /\[\^([^\s\]^]+)\]$/;

export const EditorInputRules = Extension.create({
  name: "editorInputRules",
  addInputRules() {
    const { editor } = this;
    return [
      new InputRule({
        find: TASK_PATTERN,
        handler: ({ state, range, match }) => {
          const { tr } = state;
          const $start = tr.doc.resolve(range.from);
          const item = $start.node(-1);
          // 只在列表项的第一个 paragraph 开头。Only at the start of a list item's first paragraph.
          if (
            $start.depth < 2 ||
            item.type.name !== "listItem" ||
            $start.index(-1) !== 0 ||
            $start.parent.type.name !== "paragraph"
          ) {
            return null;
          }
          tr.delete(range.from, range.to);
          tr.setNodeMarkup($start.before(-1), undefined, {
            ...item.attrs,
            checked: match[1] !== " ",
          });
        },
      }),
      new InputRule({
        find: FOOTNOTE_PATTERN,
        handler: ({ state, range, match }) => {
          const label = match[1];
          if (!markdownDefinitionsKey.getState(editor.state)!.footnotes.has(label)) return null;
          state.tr.replaceWith(
            range.from,
            range.to,
            state.schema.nodes.footnoteReference.create({ label }),
          );
        },
      }),
    ];
  },
});
