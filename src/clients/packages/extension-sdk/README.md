# @agw/extension-sdk

The Extension API types, RPC protocol, permissions and browser SDK runtime. Shared execution types come from `@agw/execution-types` through type-only imports.

## Workspace tabs

`workspace.openMainView(id, params?, options?)` opens an extension main view in the current Project's tab group. `params` accepts any JSON value up to 64 KiB when serialized as UTF-8. Desktop saves the latest parameters with the tab and supplies them to the view when it loads again. Reopening an existing tab activates it and passes the latest parameters to `onShow` if its page is already loaded.

- `key` identifies separate tabs for the same view and accepts 1–256 characters. When omitted, the view uses its default tab.
- `title` sets the tab title and accepts 1–100 characters. New tabs use the view's title by default.
- `preview` opens a preview tab when `true`. The default is `false`, which keeps the tab open. Reopening a tab with `preview: false` keeps it open.

`workspace.openFile(uri, options?)` opens or activates a cloud file tab in the current Server Profile and Project. It accepts the same `preview` option, also `false` by default. The next preview replaces the current preview. Editing a file keeps its tab open, and opening another file leaves the current file's edits in its own tab.

```ts
await api.workspace.openMainView("file", { repository, branch, path }, {
  key: `${repository}/${branch}/${path}`,
  title: fileName,
  preview: true,
});

await api.workspace.openFile(uri, { preview: true });
```

## Floating Chat

Declare `chat.composer` in the extension manifest and create the floating button from a view:

```ts
let floatingChat;

defineView({
  async onLoad(api) {
    floatingChat = await api.chat.createFloatingChat({
      context: { projectRelativePath: "plans/example.md" },
    });
  },
  async onUnload() {
    await floatingChat?.dispose();
  },
});
```

The button opens the Project's floating conversation over the view. Its conversation and draft are separate from conversation tabs and restored with the Project's tab group. The user sends messages through the host composer. Opening the same conversation in a main workspace tab starts a new floating draft.

Desktop manages the icon button for the current view. Drag it across the application window, including the Ribbon and sidebars. Releasing it docks it to the nearest left or right window edge while keeping its vertical position inside the window. Dragging does not open Chat; clicking the button or activating it with the keyboard opens the panel.

Call `setContext({ projectRelativePath: nextPath })` when the displayed document changes. Context replaces the previous values; an empty object clears it. Context is attached to each submitted message together with any pending file comments. Queued messages retain the context captured at submission.

The handle also provides `open()`, `close()` and `dispose()`. Closing preserves the conversation draft and running execution. Disposing removes the button and releases its panel association. Background pages cannot create this component. Hiding or unloading a view, or changing Project or Server, closes its panel and clears the associated context.

The host renders the panel over native extension views using the existing overlay snapshot mechanism. Closing the panel restores view interaction.
