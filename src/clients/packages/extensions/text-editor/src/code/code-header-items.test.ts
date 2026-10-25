import assert from "node:assert/strict";
import { after, test } from "node:test";

import type { DocumentSession, FileHeaderItem, TextDocumentAdapter } from "@agw/extension-sdk";

import { createEditHost, fileUri, settle, until } from "../editor/test-edit-host";
import type { CodeHeaderActions, CodeHeaderState } from "./code-header-items";

// React 在 jsdom 的全局对象准备好之后才导入。React is imported only after the jsdom globals are ready.
const { act, createElement } = await import("react");
const { createRoot } = await import("react-dom/client");
const { useCodeHeaderItems } = await import("./code-header-items");

const host = createEditHost();
after(() => host.close());

/** 以字符串保存内容的编辑器。An editor keeping its content as a string. */
class TextEditor implements TextDocumentAdapter<string> {
  public text = "";
  private edits = 0;
  public load(content: { text: string }): void {
    this.text = content.text;
  }
  public snapshot(): string {
    return this.text;
  }
  public serialize(snapshot: string): string {
    return snapshot;
  }
  public matches(snapshot: string): boolean {
    return this.text === snapshot;
  }
  public version(): number {
    return this.edits;
  }
  public type(text: string): void {
    this.text += text;
    this.edits += 1;
  }
}

function Items(props: {
  session: DocumentSession;
  state: CodeHeaderState;
  actions: CodeHeaderActions;
}) {
  useCodeHeaderItems(host.api, props.session, props.state, props.actions);
  return null;
}

/** 打开文件、等到读取完成，并渲染使用 `useCodeHeaderItems` 的组件。Opens a file, waits for the read and renders the hook. */
async function mount(name: string, state: CodeHeaderState, actions: CodeHeaderActions) {
  host.files.set(name, "const a = 1;\n");
  const editor = new TextEditor();
  const session = host.api.documents.openText({
    file: { uri: fileUri(name), name },
    adapter: editor,
  });
  await until(() => session.state === "clean", `${name} to load`);
  const container = window.document.createElement("div");
  const root = createRoot(container);
  const render = (next: CodeHeaderState, nextActions = actions) =>
    act(async () => {
      root.render(createElement(Items, { session, state: next, actions: nextActions }));
      await settle();
    });
  await render(state);
  return {
    editor,
    session,
    render,
    unmount: () =>
      act(async () => {
        root.unmount();
        await settle();
      }),
  };
}

function codeItems(canFormat: boolean, wordWrap: boolean, dirty: boolean): FileHeaderItem[] {
  return [
    { id: "format", placement: "top", label: "Format", disabled: !canFormat },
    { id: "word-wrap", placement: "top", label: "Word wrap", checked: wordWrap },
    { id: "save", placement: "top", label: "Save", disabled: !dirty },
  ];
}

const NO_ACTIONS: CodeHeaderActions = { format: () => undefined, toggleWordWrap: () => undefined };

test("the Code items show while the document shows and follow Format, Word wrap and unsaved changes", async () => {
  const sent = host.fileHeaderItems.length;
  const view = await mount(
    "items.ts",
    { shown: false, canFormat: false, wordWrap: false },
    NO_ACTIONS,
  );
  assert.deepEqual(host.fileHeaderItems.slice(sent), [[]]);

  await view.render({ shown: true, canFormat: false, wordWrap: false });
  assert.deepEqual(host.fileHeaderItems.at(-1), codeItems(false, false, false));

  // 输入后 Save 可用。Save becomes available after typing.
  await act(async () => {
    view.editor.type("x");
    view.session.edited();
    await settle();
  });
  assert.equal(view.session.state, "dirty");
  assert.deepEqual(host.fileHeaderItems.at(-1), codeItems(false, false, true));

  await view.render({ shown: true, canFormat: true, wordWrap: true });
  assert.deepEqual(host.fileHeaderItems.at(-1), codeItems(true, true, true));

  await view.unmount();
  assert.deepEqual(host.fileHeaderItems.at(-1), []);
  view.session.dispose();
});

test("choosing the Code items formats, toggles Word wrap and saves with the latest actions", async () => {
  const calls: string[] = [];
  const view = await mount(
    "choose.ts",
    { shown: true, canFormat: true, wordWrap: false },
    { format: () => calls.push("format"), toggleWordWrap: () => calls.push("wrap") },
  );
  host.selectFileHeaderItem("format", null);
  host.selectFileHeaderItem("word-wrap", null);
  await until(() => calls.length === 2, "the format and word wrap calls");
  assert.deepEqual(calls, ["format", "wrap"]);

  // 重新渲染后选择使用新的操作。After a render, choices use the new actions.
  await view.render(
    { shown: true, canFormat: true, wordWrap: true },
    { format: () => calls.push("format again"), toggleWordWrap: () => calls.push("wrap again") },
  );
  host.selectFileHeaderItem("format", null);
  await until(() => calls.length === 3, "the second format call");
  assert.equal(calls.at(-1), "format again");

  const writes = host.writes.length;
  await act(async () => {
    view.editor.type("// saved\n");
    view.session.edited();
    await settle();
  });
  host.selectFileHeaderItem("save", null);
  await until(() => view.session.state === "clean", "the save");
  assert.deepEqual(host.writes.slice(writes), [
    { name: "choose.ts", text: "const a = 1;\n// saved\n" },
  ]);

  // 其他条目不执行操作。Other items do nothing.
  host.selectFileHeaderItem("encoding", "reopen:utf-8");
  await settle();
  assert.equal(calls.length, 3);
  await view.unmount();
  view.session.dispose();
});
