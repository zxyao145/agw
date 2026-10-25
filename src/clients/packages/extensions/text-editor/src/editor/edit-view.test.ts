import assert from "node:assert/strict";
import { after, test } from "node:test";

import { createEditHost, fileUri, pressKey, settle, typeText, until } from "./test-edit-host";

// React 在 jsdom 的 document 中渲染；react-dom 在全局对象准备好之后才导入。
// React renders into jsdom's document; react-dom is imported only after the globals are ready.
const { act, createElement, Profiler } = await import("react");
const { createRoot } = await import("react-dom/client");
const { EditDocument } = await import("./edit-document");
const { EditView, unmountEditView } = await import("./edit-view");

const host = createEditHost();
const cleanups: (() => Promise<void>)[] = [];
after(async () => {
  for (const cleanup of cleanups) await cleanup();
  host.close();
});

async function mount(name: string, text: string | Uint8Array) {
  host.files.set(name, text);
  const document = new EditDocument(host.api, { uri: fileUri(name), name });
  const container = window.document.createElement("div");
  window.document.body.append(container);
  const root = createRoot(container);
  // Edit 视图中每次 React 提交都调用 onRender。onRender runs for every React commit in the Edit view.
  const commits = { count: 0 };
  await act(async () =>
    root.render(
      createElement(
        Profiler,
        { id: "edit", onRender: () => (commits.count += 1) },
        createElement(EditView, { api: host.api, editDocument: document }),
      ),
    ),
  );
  cleanups.push(async () => {
    await act(async () => root.unmount());
    document.dispose();
    container.remove();
  });
  await act(() => until(() => document.session.state !== "loading", `${name} to load`));
  return { document, container, commits };
}

/** `/` 菜单中的项目文字，选中项在前面加 "> "。The `/` menu entries, the selected one prefixed with "> ". */
function slashMenu(): string[] {
  const menu = window.document.querySelector(".slash-menu");
  if (!menu) return [];
  return [...menu.querySelectorAll("[role=option]")].map(
    (option) =>
      `${option.getAttribute("aria-selected") === "true" ? "> " : ""}${option.textContent}`,
  );
}

test("the editing area uses the shared Markdown typography in document density", async () => {
  const { container } = await mount("typography.md", "# Title\n\nText.\n");
  const editorElement = container.querySelector(".ProseMirror")!;
  assert.ok(editorElement.classList.contains("agw-prose"));
  assert.equal(editorElement.getAttribute("data-density"), "document");
});

test("the / menu filters by the typed text and is driven by the arrow keys and Enter", async () => {
  const { document } = await mount("slash.md", "First.\n");
  const { editor } = document;
  await act(async () => {
    editor.commands.setTextSelection(1 + "First.".length);
    pressKey(editor, "Enter");
    typeText(editor, "/");
    await settle();
  });
  assert.deepEqual(slashMenu(), [
    "> Text",
    "Heading 1",
    "Heading 2",
    "Heading 3",
    "Bulleted list",
    "Numbered list",
    "To-do list",
    "Quote",
    "Code block",
    "Table",
    "Divider",
    "Image",
    "HTML block",
  ]);
  await act(async () => {
    typeText(editor, "head");
    await settle();
  });
  assert.deepEqual(slashMenu(), ["> Heading 1", "Heading 2", "Heading 3"]);
  await act(async () => {
    pressKey(editor, "ArrowDown");
    pressKey(editor, "ArrowDown");
    pressKey(editor, "ArrowUp");
    await settle();
  });
  assert.deepEqual(slashMenu(), ["Heading 1", "> Heading 2", "Heading 3"]);
  await act(async () => {
    pressKey(editor, "Enter");
    await settle();
  });
  assert.deepEqual(slashMenu(), []);
  const second = editor.state.doc.child(1);
  assert.equal(second.type.name, "heading");
  assert.equal(second.attrs.level, 2);
  assert.equal(second.textContent, "");

  // 没有匹配项时不显示列表；Esc 关闭菜单并保留文字。No list without matches; Esc closes it and keeps the text.
  await act(async () => {
    typeText(editor, "Title ");
    typeText(editor, "/zzz");
    await settle();
  });
  assert.deepEqual(slashMenu(), []);
  await act(async () => {
    const { from } = editor.state.selection;
    editor.commands.deleteRange({ from: from - "/zzz".length, to: from });
    typeText(editor, "/quo");
    await settle();
  });
  assert.deepEqual(slashMenu(), ["> Quote"]);
  await act(async () => {
    pressKey(editor, "Escape");
    await settle();
  });
  assert.deepEqual(slashMenu(), []);
  assert.equal(editor.state.doc.child(1).textContent, "Title /quo");
});

test("the / menu's image address input follows later edits and closes once its position is gone", async () => {
  const { document } = await mount("image-insert.md", "First.\n\nSecond.\n");
  const { editor } = document;
  const openImageInput = async () => {
    await act(async () => {
      editor.commands.setTextSelection(1 + editor.state.doc.child(0).content.size);
      pressKey(editor, "Enter");
      typeText(editor, "/image");
      await settle();
    });
    assert.deepEqual(slashMenu(), ["> Image"]);
    await act(async () => {
      pressKey(editor, "Enter");
      await settle();
    });
    const input = window.document.querySelector<HTMLInputElement>(
      '.image-insert input[aria-label="Image address"]',
    );
    assert.ok(input, "the / menu's Image opens the address input");
    return input;
  };

  // 输入框打开后在前面输入文字：图片仍插入在空 paragraph 中。Text typed before the position while the input is open:
  // the image still goes into the empty paragraph.
  const input = await openImageInput();
  await act(async () => {
    editor.view.dispatch(editor.state.tr.insertText("Very ", 1));
  });
  await act(async () => {
    setInput(input, "a.png");
    input.form!.requestSubmit();
  });
  assert.deepEqual(
    [editor.state.doc.child(0).textContent, editor.state.doc.child(1).firstChild?.attrs.src],
    ["Very First.", "a.png"],
  );

  // 输入框所在的 paragraph 被删除：输入框关闭。The input's paragraph is deleted: the input closes.
  await openImageInput();
  const empty = editor.state.doc.child(0).nodeSize;
  assert.equal(editor.state.doc.child(1).content.size, 0);
  await act(async () => {
    editor.view.dispatch(editor.state.tr.delete(empty, empty + editor.state.doc.child(1).nodeSize));
  });
  assert.equal(window.document.querySelector(".image-insert"), null);

  // 输入框所在的 paragraph 改为代码块，那里不能放图片：输入框关闭。The input's paragraph becomes a code block, which
  // cannot hold an image: the input closes.
  await openImageInput();
  await act(async () => {
    editor.view.dispatch(
      editor.state.tr.setBlockType(empty + 1, empty + 1, editor.schema.nodes.codeBlock),
    );
  });
  assert.equal(editor.state.doc.child(1).type.name, "codeBlock");
  assert.equal(window.document.querySelector(".image-insert"), null);
});

test("a load failure shows the error panel and hides the editor", async () => {
  const { container } = await mount("broken.md", "---\ntitle: [unclosed\n---\n");
  const panel = container.querySelector(".document-error");
  assert.ok(panel);
  assert.match(
    panel.textContent ?? "",
    /^Could not read broken\.mdFront matter isn't valid YAML: /,
  );
  assert.equal(container.querySelector<HTMLElement>(".edit-scroll")?.hidden, true);
});

/** 等待浮层（BubbleMenu 默认延迟 250 ms 更新）。Waits for the popovers (BubbleMenu updates after 250 ms). */
async function waitForPopovers(): Promise<void> {
  await act(() => new Promise((resolve) => setTimeout(resolve, 320)));
}

/** 像用户输入一样修改 React 管理的输入框。Changes a React-controlled input the way typing does. */
function setInput(input: HTMLInputElement, value: string): void {
  const setter = Object.getOwnPropertyDescriptor(window.HTMLInputElement.prototype, "value")!.set!;
  setter.call(input, value);
  input.dispatchEvent(new window.Event("input", { bubbles: true }));
}

function button(root: Element, text: string): HTMLButtonElement {
  const found = [...root.querySelectorAll("button")].find(
    (element) => element.textContent === text,
  );
  assert.ok(found, `No button "${text}" in "${root.textContent}".`);
  return found;
}

/** 文字为 `text` 的文本节点的链接标记属性。The link mark attributes of the text node reading `text`. */
function linkOf(document: InstanceType<typeof EditDocument>, text: string) {
  let attrs: Record<string, unknown> | null | undefined;
  document.editor.state.doc.descendants((node) => {
    if (attrs === undefined && node.isText && node.text === text) {
      attrs = node.marks.find((mark) => mark.type.name === "link")?.attrs ?? null;
    }
  });
  assert.notEqual(attrs, undefined, `No text node reads "${text}".`);
  return attrs;
}

test("Mod+K and the link popover add, open, edit and remove links", async () => {
  const { document } = await mount(
    "links.md",
    "See the docs here.\n\nA [reference][ref] link.\n\n[ref]: https://example.com/ref\n",
  );
  const { editor } = document;
  const docs = 1 + "See the ".length;
  await act(async () => {
    editor.view.focus();
    editor.commands.setTextSelection({ from: docs, to: docs + "docs".length });
  });
  await waitForPopovers();
  await act(async () => {
    pressKey(editor, "k", { mod: true });
  });
  const linkInput = window.document.querySelector<HTMLInputElement>(
    '.formatting-menu input[aria-label="Link address"]',
  );
  assert.ok(linkInput, "Mod+K opens the link input");
  await act(async () => {
    setInput(linkInput, "docs/guide.md");
    linkInput.form!.requestSubmit();
  });
  assert.deepEqual(linkOf(document, "docs")?.href, "docs/guide.md");

  // 光标在链接中：地址、Open（相对路径打开项目文件）、Edit、Remove。
  // Cursor in the link: the address, Open (a relative path opens the project file), Edit, Remove.
  await act(async () => {
    editor.view.focus();
    editor.commands.setTextSelection(docs + 2);
  });
  await waitForPopovers();
  let menu = window.document.querySelector(".link-menu")!;
  assert.equal(menu.textContent, "docs/guide.mdOpenEditRemove");
  host.opened.length = 0;
  await act(async () => {
    button(menu, "Open").click();
    await settle();
  });
  assert.deepEqual(host.opened, [
    { method: "workspace.openFile", target: fileUri("docs/guide.md") },
  ]);
  await act(async () => {
    button(menu, "Remove").click();
  });
  assert.equal(linkOf(document, "See the docs here."), null);

  // 引用式链接：显示解析的地址与标签，Open 打开外部地址，Edit 改为普通链接。
  // A reference link: the resolved address and the label, Open goes external, Edit makes a plain link.
  const reference = 1 + "See the docs here.".length + 2 + "A ".length;
  await act(async () => {
    editor.view.focus();
    editor.commands.setTextSelection(reference + 3);
  });
  await waitForPopovers();
  menu = window.document.querySelector(".link-menu")!;
  assert.equal(menu.textContent, "https://example.com/ref[ref]OpenEditRemove");
  host.opened.length = 0;
  await act(async () => {
    button(menu, "Open").click();
    await settle();
  });
  assert.deepEqual(host.opened, [{ method: "ui.openExternal", target: "https://example.com/ref" }]);
  await act(async () => {
    button(menu, "Edit").click();
  });
  const editInput = menu.querySelector<HTMLInputElement>('input[aria-label="Link address"]')!;
  assert.equal(editInput.value, "https://example.com/ref");
  await act(async () => {
    setInput(editInput, "https://example.org/new");
    editInput.form!.requestSubmit();
  });
  const edited = linkOf(document, "reference")!;
  assert.deepEqual(
    [edited.href, edited.reference, edited.referenceKind],
    ["https://example.org/new", null, null],
  );
});

test("applying the address a reference link resolves to keeps its reference spelling", async () => {
  const { document } = await mount(
    "keep-reference.md",
    "A [reference][ref] link.\n\n[ref]: https://example.com/ref\n",
  );
  const { editor } = document;
  const reference = 1 + "A ".length;
  const spelling = () => {
    const attrs = linkOf(document, "reference")!;
    return [attrs.href, attrs.reference, attrs.referenceKind];
  };
  const loaded = ["https://example.com/ref", "ref", "full"];
  assert.deepEqual(spelling(), loaded);
  // 链接浮层：Edit 后不改地址直接 Apply。The link popover: Edit, then Apply without changing the address.
  await act(async () => {
    editor.view.focus();
    editor.commands.setTextSelection(reference + 3);
  });
  await waitForPopovers();
  const menu = window.document.querySelector(".link-menu")!;
  await act(async () => {
    button(menu, "Edit").click();
  });
  const menuInput = menu.querySelector<HTMLInputElement>('input[aria-label="Link address"]')!;
  assert.equal(menuInput.value, "https://example.com/ref");
  await act(async () => {
    menuInput.form!.requestSubmit();
  });
  assert.deepEqual(spelling(), loaded);

  // 格式工具条：选中链接文字后 Mod+K，输入框显示解析的地址，直接 Apply。The formatting toolbar: Mod+K on the
  // selected link text shows the resolved address, then Apply.
  await act(async () => {
    editor.view.focus();
    editor.commands.setTextSelection({ from: reference, to: reference + "reference".length });
  });
  await waitForPopovers();
  await act(async () => {
    pressKey(editor, "k", { mod: true });
  });
  const toolbarInput = window.document.querySelector<HTMLInputElement>(
    '.formatting-menu input[aria-label="Link address"]',
  )!;
  assert.equal(toolbarInput.value, "https://example.com/ref");
  await act(async () => {
    toolbarInput.form!.requestSubmit();
  });
  assert.deepEqual(spelling(), loaded);
  assert.equal(document.session.state, "clean");

  // 定义修改后，工具条显示新定义的地址，直接 Apply 仍保留引用写法。After the definition changes, the toolbar shows
  // the new definition's address, and Apply still keeps the reference spelling.
  let definition = -1;
  editor.state.doc.forEach((node, offset) => {
    if (node.type.name === "rawBlock") definition = offset;
  });
  await act(async () => {
    const node = editor.state.doc.nodeAt(definition)!;
    editor.view.dispatch(
      editor.state.tr.insertText(
        "[ref]: https://example.com/new",
        definition + 1,
        definition + 1 + node.content.size,
      ),
    );
    editor.view.focus();
    editor.commands.setTextSelection({ from: reference, to: reference + "reference".length });
  });
  await waitForPopovers();
  await act(async () => {
    pressKey(editor, "k", { mod: true });
  });
  const changedInput = window.document.querySelector<HTMLInputElement>(
    '.formatting-menu input[aria-label="Link address"]',
  )!;
  assert.equal(changedInput.value, "https://example.com/new");
  await act(async () => {
    changedInput.form!.requestSubmit();
  });
  assert.deepEqual(spelling(), loaded);
});

test("Mod+K is left to the browser inside code blocks", async () => {
  const { document } = await mount("code-link.md", "```\ncode here\n```\n");
  let event: KeyboardEvent | null = null;
  await act(async () => {
    document.editor.commands.setTextSelection({ from: 2, to: 6 });
    event = pressKey(document.editor, "k", { mod: true });
  });
  assert.equal(event!.defaultPrevented, false);
});

test("the first edit after loading dispatches only its own transaction", async () => {
  // 未保存状态的出现只更新顶部条；编辑器中的菜单不重新渲染，也不因此派发事务。The unsaved state appearing updates
  // only the top bar; the editor's menus do not render again or dispatch transactions because of it.
  const { document } = await mount("transition.md", "Text.\n");
  const transactions: string[] = [];
  document.editor.on("transaction", ({ transaction }) =>
    transactions.push(transaction.docChanged ? "edit" : "other"),
  );
  await act(async () => {
    document.editor.commands.setTextSelection(3);
    await settle();
  });
  transactions.length = 0;
  await act(async () => {
    typeText(document.editor, "x");
    await settle();
  });
  assert.equal(document.session.state, "dirty");
  assert.deepEqual(transactions, ["edit"]);
});

test("the session reports every save state to the host, and Save follows without a React commit", async () => {
  const { document, container, commits } = await mount("states.md", "Text.\n");
  const save = [...container.querySelectorAll<HTMLButtonElement>(".edit-toolbar button")].find(
    (element) => element.textContent === "Save",
  )!;
  const seen: string[] = [];
  const record = () => {
    const { state, changedOnDisk } = document.session;
    seen.push(`${state}${changedOnDisk ? "+changed" : ""}: save ${save.disabled ? "off" : "on"}`);
  };
  // 会话的监听按订阅顺序调用，组件的监听在前。Session listeners run in subscription order, the component's first.
  document.session.onDidChange(record);
  await act(async () => {
    document.editor.commands.setTextSelection(3);
    await settle();
  });
  const before = commits.count;
  const reported = host.saveStatuses.length;
  record();
  await act(async () => {
    typeText(document.editor, "x");
    await settle();
  });
  await act(async () => {
    await document.session.save();
    await settle();
  });
  // 刷新读取期间有输入：读取结果不替换内容，标记磁盘已变化。Typing during a refresh read keeps the content and marks the
  // disk as changed.
  host.files.set("states.md", "Changed on disk.\n");
  await act(async () => {
    const refresh = document.show({ uri: fileUri("states.md"), name: "states.md" });
    typeText(document.editor, "y");
    await refresh;
    await settle();
  });
  assert.deepEqual(seen, [
    "clean: save off",
    "dirty: save on",
    "saving: save off",
    "clean: save off",
    "dirty: save on",
    "dirty+changed: save on",
  ]);
  assert.deepEqual(host.saveStatuses.slice(reported), [
    "unsaved",
    "saving",
    "saved",
    "unsaved",
    "changed-on-disk",
  ]);
  assert.equal(commits.count, before, "a session state change commits nothing in React");

  // 宿主文件名后的 Reload 先确认放弃修改。The Reload after the host's file name asks to discard the changes first.
  host.answers.push(1);
  const boxes = host.messageBoxes.length;
  await act(async () => {
    host.requestDocumentReload();
    await settle();
  });
  assert.equal(host.messageBoxes.length, boxes + 1);
  assert.deepEqual(host.messageBoxes.at(-1), {
    type: "warning",
    message: "Discard unsaved changes to states.md?",
    buttons: ["Discard", "Cancel"],
    defaultId: 1,
    cancelId: 1,
  });
});

test("a document that fails to load reports no save status and disables Save", async () => {
  const reported = host.saveStatuses.length;
  const { container } = await mount("binary.md", new Uint8Array([0, 1, 2]));
  await act(settle);
  assert.deepEqual(host.saveStatuses.slice(reported), []);
  const save = [...container.querySelectorAll<HTMLButtonElement>(".edit-toolbar button")].find(
    (element) => element.textContent === "Save",
  )!;
  assert.equal(save.disabled, true);
});

test("the top bar shows the encoding and the Save button", async () => {
  const { document, container } = await mount("chrome.md", "Text.\n");
  const bar = container.querySelector(".edit-toolbar")!;
  assert.equal(bar.textContent, "UTF-8Save");
  await act(async () => {
    document.editor.commands.setTextSelection(3);
    typeText(document.editor, "x");
    await settle();
  });
  assert.equal(bar.textContent, "UTF-8Save");
});

test("unloading the Edit view destroys the editor without drawing the document again", async () => {
  // 列表项、代码块与图片都由节点视图绘制。List items, code blocks and images are drawn by node views.
  host.files.set(
    "unload.md",
    "- item one\n- item two\n\n```ts\nconst a = 1;\n```\n\n![alt](https://example.com/a.png)\n\nText.\n",
  );
  const document = new EditDocument(host.api, { uri: fileUri("unload.md"), name: "unload.md" });
  const container = window.document.createElement("div");
  window.document.body.append(container);
  const root = createRoot(container);
  await act(async () =>
    root.render(createElement(EditView, { api: host.api, editDocument: document })),
  );
  await act(() => until(() => document.session.state !== "loading", "unload.md to load"));
  const editorElement = container.querySelector(".ProseMirror")!;
  assert.ok(editorElement.querySelector(".edit-image"));
  const observer = new window.MutationObserver(() => undefined);
  observer.observe(editorElement, { childList: true, subtree: true });

  // 与 onUnload 相同，在 act 之外调用：act 中的 root.unmount() 推迟到 act 结束，onUnload 中它同步执行。
  // Called outside act as in onUnload: inside act root.unmount() is deferred until act ends, in onUnload it runs
  // synchronously.
  const scope = globalThis as { IS_REACT_ACT_ENVIRONMENT?: boolean };
  scope.IS_REACT_ACT_ENVIRONMENT = false;
  try {
    unmountEditView(root, document);
  } finally {
    scope.IS_REACT_ACT_ENVIRONMENT = true;
  }
  const added = observer
    .takeRecords()
    .reduce((count, record) => count + record.addedNodes.length, 0);
  observer.disconnect();
  container.remove();
  assert.equal(document.editor.isDestroyed, true);
  assert.equal(added, 0);
});
