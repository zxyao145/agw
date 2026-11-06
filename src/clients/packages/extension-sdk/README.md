# Floating Chat

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

The button opens the current Project's current conversation over the view. Messages, drafts, Agent selection and execution state are shared with the Chat page. The user sends messages through the host composer.

Call `setContext({ projectRelativePath: nextPath })` when the displayed document changes. Context replaces the previous values; an empty object clears it. Context is attached to each submitted message together with any pending file comments. Queued messages retain the context captured at submission.

The handle also provides `open()`, `close()` and `dispose()`. Closing preserves the conversation draft and running execution. Disposing removes the button and releases its panel association. Background pages cannot create this component. Hiding or unloading a view, or changing Project or Server, closes its panel and clears the associated context.

The host renders the panel over native extension views using the existing overlay snapshot mechanism. Closing the panel restores view interaction.
