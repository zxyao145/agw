import type { ExtensionApi } from "@agw/extension-sdk";
import { Extension, type AnyExtension, type Node } from "@tiptap/core";
import { ListKeymap } from "@tiptap/extension-list";
import { Plugin, PluginKey } from "@tiptap/pm/state";
import { Dropcursor, Gapcursor, Placeholder, TrailingNode, UndoRedo } from "@tiptap/extensions";

import { canTurnIntoSelection, turnInto } from "./block-commands";
import { CodeBlockView } from "./code-block-view";
import { codeLanguages } from "./code-languages";
import type { EditUi } from "./edit-ui";
import { editorExtensions } from "./editor-extensions";
import { EditorInputRules } from "./editor-input-rules";
import { FootnoteReference, FrontMatter, RawBlock } from "./editor-nodes";
import { ImageView } from "./image-view";
import { currentLink, linkAt, openLink } from "./link-commands";
import { ListItemView } from "./list-item-view";
import { MarkdownPaste } from "./markdown-paste";
import { MermaidEditing } from "./mermaid-editing";
import { RawBlockView } from "./raw-block-view";
import { ReferenceDecorations } from "./reference-decorations";
import { createSlashCommand, type SlashMenuStore } from "./slash-command";
import { TableNormalization } from "./table-normalization";

// Mixed 编辑器的扩展：Task 11 的 schema、解析与输出（editorExtensions），其中代码块、列表项、图片与原文块换成带 node view
// 的扩展；再加上只用于编辑的扩展（设计 5.7 与 Tiptap 选型）。node 的 schema 不变，载入与写回使用同一个 schema。
// The Mixed editor's extensions: Task 11's schema, parsing and output (editorExtensions), with the code block, list
// item, image and raw block nodes extended by node views; plus the editing-only extensions (design section 5.7 and
// the Tiptap choices). Node schemas stay the same, so loading and writing use the same schema.

export type EditContext = {
  readonly api: ExtensionApi;
  readonly ui: EditUi;
  readonly slashMenu: SlashMenuStore;
  /** 当前文件的 URI；切换文件后随之改变。The current file's URI; it changes with a file switch. */
  fileUri(): string;
};

const PLACEHOLDER = "Type '/' for commands";

/** 按住 Mod（macOS 为 Cmd，其他平台为 Ctrl）。Whether Mod is held (Cmd on macOS, Ctrl elsewhere). */
function modHeld(api: ExtensionApi, event: MouseEvent): boolean {
  return api.context.get().app.platform === "darwin" ? event.metaKey : event.ctrlKey;
}

function withNodeViews(extension: AnyExtension, context: EditContext): AnyExtension {
  if (extension === RawBlock || extension === FrontMatter) {
    return (extension as Node).extend({
      addNodeView() {
        return ({ node }) => new RawBlockView(node);
      },
    });
  }
  if (extension === FootnoteReference) {
    // 上标样式的 `[^label]`。A superscript `[^label]`.
    return FootnoteReference.extend({
      renderHTML: ({ node }) => [
        "sup",
        { "data-footnote-reference": node.attrs.label, class: "footnote-reference" },
        `[^${node.attrs.label}]`,
      ],
    });
  }
  switch (extension.name) {
    case "codeBlock":
      return (extension as Node).extend({
        addNodeView() {
          const languages = codeLanguages(this.options.lowlight.listLanguages());
          return ({ node, decorations, editor, getPos }) =>
            new CodeBlockView(node, decorations, editor, getPos, context.api, languages);
        },
      });
    case "listItem":
      return (extension as Node).extend({
        addNodeView() {
          return ({ node, view, getPos }) => new ListItemView(node, view, getPos);
        },
        addKeyboardShortcuts() {
          return {
            ...this.parent?.(),
            // 任务项中按 Enter 得到未勾选的任务项。列表项不能分开时不处理这个键，也不发出事务（命令失败时 Tiptap
            // 仍会发出事务）。
            // Enter in a task item gives an unchecked task item. When the list item cannot be split, the key is left
            // alone and no transaction is sent (Tiptap sends one even when a command fails).
            Enter: () => {
              if (!this.editor.can().splitListItem(this.name)) return false;
              return this.editor.commands.command(({ tr, commands }) => {
                commands.splitListItem(this.name);
                const { $from } = tr.selection;
                const item = $from.node(-1);
                if (item.type.name === this.name && item.attrs.checked === true) {
                  tr.setNodeMarkup($from.before(-1), undefined, { ...item.attrs, checked: false });
                }
                return true;
              });
            },
          };
        },
      });
    case "image":
      return (extension as Node).extend({
        addNodeView() {
          return ({ node, decorations }) =>
            new ImageView(node, decorations, context.api, context.fileUri);
        },
      });
    default:
      return extension;
  }
}

/** `Mod+K`、`Mod+Shift+9` 与 `Mod+点击` 打开链接。`Mod+K`, `Mod+Shift+9` and `Mod+click` to open links. */
function editShortcuts(context: EditContext): Extension {
  return Extension.create({
    name: "editShortcuts",
    addKeyboardShortcuts() {
      return {
        // 有选中的文字或光标在链接中时打开地址输入框；代码块、rawBlock 与 frontMatter 中没有链接。Opens the address
        // input for selected text or a link around the cursor; code blocks, rawBlock and frontMatter have no links.
        "Mod-k": () => {
          const { state } = this.editor;
          const { $from, $to, empty } = state.selection;
          if ($from.parent.type.spec.code || $to.parent.type.spec.code) return false;
          return (!empty || currentLink(state) !== null) && context.ui.requestLinkEditor();
        },
        "Mod-Shift-9": () => {
          if (!canTurnIntoSelection(this.editor.state)) return false;
          turnInto(this.editor, "taskList");
          return true;
        },
      };
    },
    addProseMirrorPlugins() {
      return [
        new Plugin({
          key: new PluginKey("openLinkOnModClick"),
          props: {
            handleClick: (view, pos, event) => {
              if (event.button !== 0 || !modHeld(context.api, event)) return false;
              const link = linkAt(view.state, pos);
              if (!link) return false;
              if (link.href) openLink(context.api, context.fileUri(), link.href);
              return true;
            },
          },
        }),
      ];
    },
  });
}

export function createEditExtensions(context: EditContext): AnyExtension[] {
  return [
    ...editorExtensions.map((extension) => withNodeViews(extension, context)),
    UndoRedo,
    Dropcursor,
    Gapcursor,
    // doc 的第一个子节点可以是 frontMatter，结尾补的节点必须明确为 paragraph。The doc may start with frontMatter, so
    // the node added at the end must be named as paragraph.
    TrailingNode.configure({ node: "paragraph" }),
    Placeholder.configure({
      includeChildren: true,
      placeholder: ({ node }) =>
        node.type.name === "heading"
          ? `Heading ${String(node.attrs.level)}`
          : node.type.name === "paragraph"
            ? PLACEHOLDER
            : "",
    }),
    ListKeymap,
    createSlashCommand(context.slashMenu, context.ui),
    EditorInputRules,
    MarkdownPaste,
    MermaidEditing,
    ReferenceDecorations,
    TableNormalization,
    editShortcuts(context),
  ];
}
