import assert from "node:assert/strict";
import { after, test } from "node:test";

import { NodeSelection } from "@tiptap/pm/state";

import { DARK_NODE, darkTheme } from "../shared/test-mermaid";
import { createEditHost, fileUri, pressKey, settle, typeText, until } from "./test-edit-host";

// React 在 jsdom 的 document 中渲染；react-dom 在全局对象准备好之后才导入。
// React renders into jsdom's document; react-dom is imported only after the globals are ready.
const { act, createElement } = await import("react");
const { createRoot } = await import("react-dom/client");
const { setCodeBlockLanguage } = await import("./code-languages");
const { EditDocument } = await import("./edit-document");
const { EditView } = await import("./edit-view");

const host = createEditHost();
const cleanups: (() => Promise<void>)[] = [];
after(async () => {
  for (const cleanup of cleanups) await cleanup();
  host.close();
});

type EditDocumentType = InstanceType<typeof EditDocument>;

const DIAGRAM = 'flowchart LR\n  a["Start"] --> b["End"]';
const SOURCE = `Before.\n\n\`\`\`mermaid\n${DIAGRAM}\n\`\`\`\n\nAfter.\n`;

async function mount(name: string, text: string) {
  host.files.set(name, text);
  const document = new EditDocument(host.api, { uri: fileUri(name), name });
  const container = window.document.createElement("div");
  window.document.body.append(container);
  const root = createRoot(container);
  await act(async () =>
    root.render(createElement(EditView, { api: host.api, editDocument: document })),
  );
  cleanups.push(async () => {
    await act(async () => root.unmount());
    document.dispose();
    container.remove();
  });
  await act(() => until(() => document.session.state !== "loading", `${name} to load`));
  return { document, container };
}

/** 第 `index` 个代码块的位置与节点。The position and node of the `index`th code block. */
function codeBlockAt(document: EditDocumentType, index = 0) {
  const blocks: { pos: number; size: number }[] = [];
  document.editor.state.doc.descendants((node, pos) => {
    if (node.type.name === "codeBlock") blocks.push({ pos, size: node.nodeSize });
    return true;
  });
  const block = blocks[index];
  assert.ok(block, `No code block ${index}.`);
  return { ...block, start: block.pos + 1, end: block.pos + block.size - 1 };
}

function blockElement(container: HTMLElement, index = 0): HTMLElement {
  const element = container.querySelectorAll<HTMLElement>(".code-block")[index];
  assert.ok(element, `No code block element ${index}.`);
  return element;
}

function svgText(block: HTMLElement): string {
  return block.querySelector(".mermaid-diagram > svg")?.outerHTML ?? "";
}

function viewToggle(block: HTMLElement): HTMLButtonElement {
  return block.querySelector<HTMLButtonElement>(".code-block-view")!;
}

async function toggleView(block: HTMLElement): Promise<void> {
  await act(async () => {
    viewToggle(block).click();
    await settle();
  });
}

/** 整体选中的代码块的位置；不是整体选中时为 null。The position of the selected block, or null without one. */
function selectedBlock(document: EditDocumentType): number | null {
  const { selection } = document.editor.state;
  return selection instanceof NodeSelection ? selection.from : null;
}

async function press(document: EditDocumentType, key: string): Promise<KeyboardEvent> {
  let event: KeyboardEvent | null = null;
  await act(async () => {
    event = pressKey(document.editor, key);
    await settle();
  });
  return event!;
}

/** 在图表区域（或其中的元素）上按下并点击。Presses and clicks on the diagram area or an element in it. */
async function clickOn(element: Element): Promise<MouseEvent> {
  const press = new window.MouseEvent("mousedown", { bubbles: true, cancelable: true, button: 0 });
  await act(async () => {
    element.dispatchEvent(press);
    element.dispatchEvent(
      new window.MouseEvent("click", { bubbles: true, cancelable: true, button: 0 }),
    );
    await settle();
  });
  return press;
}

async function rendered(block: HTMLElement, includes: string): Promise<void> {
  await act(() =>
    until(
      () => block.dataset.diagram === "rendered" && svgText(block).includes(includes),
      `the diagram with ${includes}`,
    ),
  );
}

async function select(document: EditDocumentType, position: number): Promise<void> {
  await act(async () => {
    document.editor.commands.setTextSelection(position);
    await settle();
  });
}

test("a Mermaid code block shows its diagram, and the source shows while the cursor is in the block", async () => {
  const { document, container } = await mount("diagram.md", SOURCE);
  const block = blockElement(container);
  await rendered(block, "Start");
  assert.equal(block.hasAttribute("data-editing"), false);

  const code = codeBlockAt(document);
  await select(document, code.start + 3);
  assert.equal(block.hasAttribute("data-editing"), true);
  await select(document, code.end + 2);
  assert.equal(block.hasAttribute("data-editing"), false);
  // 移动光标不修改文档。Moving the cursor does not change the document.
  assert.equal(document.session.state, "clean");
});

test("typing in a Mermaid code block renders the new source, shows syntax errors, and writes back only the code", async () => {
  host.writes.length = 0;
  const { document, container } = await mount("typing.md", SOURCE);
  const block = blockElement(container);
  await rendered(block, "Start");

  await act(async () => {
    document.editor.commands.setTextSelection(codeBlockAt(document).end);
    typeText(document.editor, '\n  b --> c["Third"]');
  });
  await rendered(block, "Third");

  await act(async () => typeText(document.editor, " -->"));
  await act(() => until(() => block.dataset.diagram === "failed", "the syntax error"));
  assert.match(block.querySelector(".mermaid-diagram-error")!.textContent!, /Parse error/);
  assert.equal(svgText(block), "");

  await document.session.save();
  assert.deepEqual(host.writes, [
    {
      name: "typing.md",
      text: `Before.\n\n\`\`\`mermaid\n${DIAGRAM}\n  b --> c["Third"] -->\n\`\`\`\n\nAfter.\n`,
    },
  ]);
});

test("clicking the diagram puts the cursor at the end of the source", async () => {
  const { document, container } = await mount("click.md", SOURCE);
  const block = blockElement(container);
  await rendered(block, "Start");
  await select(document, 1);

  const event = new window.MouseEvent("mousedown", { bubbles: true, cancelable: true, button: 0 });
  await act(async () => {
    block.querySelector(".mermaid-diagram")!.dispatchEvent(event);
    await settle();
  });
  assert.equal(event.defaultPrevented, true);
  const { from, to } = document.editor.state.selection;
  assert.deepEqual([from, to], [codeBlockAt(document).end, codeBlockAt(document).end]);
  assert.equal(block.hasAttribute("data-editing"), true);
});

test("arrow keys at a textblock edge enter a Mermaid code block, and other code blocks are left to the browser", async () => {
  const source = `${SOURCE}\n\`\`\`ts\nconst a = 1;\n\`\`\`\n\nLast.\n`;
  const { document } = await mount("arrows.md", source);
  const { editor } = document;
  const mermaid = codeBlockAt(document, 0);
  const typescript = codeBlockAt(document, 1);
  const beforeEnd = 1 + "Before.".length;
  const afterStart = mermaid.pos + mermaid.size + 1;

  const cases: { from: number; key: string; to: number }[] = [
    { from: beforeEnd, key: "ArrowRight", to: mermaid.start },
    { from: beforeEnd, key: "ArrowDown", to: mermaid.start },
    { from: afterStart, key: "ArrowLeft", to: mermaid.end },
    { from: afterStart, key: "ArrowUp", to: mermaid.end },
  ];
  for (const { from, key, to } of cases) {
    await select(document, from);
    let event: KeyboardEvent | null = null;
    await act(async () => {
      event = pressKey(editor, key);
    });
    assert.equal(event!.defaultPrevented, true, `${key} from ${from}`);
    assert.equal(editor.state.selection.head, to, `${key} from ${from}`);
  }

  // 普通代码块之前的 paragraph 末尾：插件不处理，光标不变。Before a plain code block the plugin leaves the key alone.
  const afterEnd = typescript.pos - 1;
  await select(document, afterEnd);
  let plain: KeyboardEvent | null = null;
  await act(async () => {
    plain = pressKey(editor, "ArrowRight");
  });
  assert.equal(plain!.defaultPrevented, false);
  assert.equal(editor.state.selection.head, afterEnd);
});

test("changing the language from mermaid removes the diagram, and changing it back shows it again", async () => {
  const { document, container } = await mount("language.md", SOURCE);
  const block = blockElement(container);
  await rendered(block, "Start");

  await toggleView(block);
  await act(async () => setCodeBlockLanguage(document.editor, codeBlockAt(document).pos, ""));
  assert.equal(block.querySelector(".mermaid-diagram"), null);
  assert.equal(block.dataset.diagram, undefined);
  assert.equal(block.dataset.view, undefined);
  assert.equal(block.hasAttribute("data-editing"), false);
  assert.equal(viewToggle(block).hidden, true);

  await act(async () =>
    setCodeBlockLanguage(document.editor, codeBlockAt(document).pos, "mermaid"),
  );
  await rendered(block, "Start");
  // 改回 mermaid 后为默认视图。Back to mermaid, the block is in the default view.
  assert.equal(block.dataset.view, undefined);
  assert.equal(viewToggle(block).getAttribute("aria-pressed"), "false");
});

test("a theme change renders the Edit diagrams in the new theme", async () => {
  const { container } = await mount("theme.md", SOURCE);
  const block = blockElement(container);
  await rendered(block, "Start");
  const light = host.api.theme.get();
  try {
    host.changeTheme(darkTheme(light));
    await act(() => until(() => DARK_NODE.test(svgText(block)), "the dark diagram"));
  } finally {
    host.changeTheme(light);
  }
  await act(() => until(() => !DARK_NODE.test(svgText(block)), "the light diagram"));
});

test("the Diagram only toggle shows only in Mermaid code blocks and switches the view without changing the document", async () => {
  const { document, container } = await mount(
    "toggle.md",
    `${SOURCE}\n\`\`\`ts\nconst a = 1;\n\`\`\`\n`,
  );
  const block = blockElement(container, 0);
  await rendered(block, "Start");
  assert.equal(viewToggle(block).hidden, false);
  assert.equal(viewToggle(block).textContent, "Diagram only");
  assert.equal(viewToggle(block).getAttribute("aria-pressed"), "false");
  assert.equal(viewToggle(blockElement(container, 1)).hidden, true);

  await toggleView(block);
  assert.equal(block.dataset.view, "diagram");
  assert.equal(viewToggle(block).getAttribute("aria-pressed"), "true");
  // 视图不写入文档，也不进入撤销历史。The view is neither in the document nor in the undo history.
  assert.equal(document.session.state, "clean");
  assert.equal(document.editor.can().undo(), false);

  await toggleView(block);
  assert.equal(block.dataset.view, undefined);
  assert.equal(viewToggle(block).getAttribute("aria-pressed"), "false");
});

test("a selection entering a Diagram only block selects the whole block and never shows the source", async () => {
  const { document, container } = await mount("whole.md", SOURCE);
  const block = blockElement(container);
  await rendered(block, "Start");
  const code = codeBlockAt(document);

  // 光标在源码中时切换：改为整体选中。Switching with the cursor in the source selects the whole block.
  await select(document, code.start + 3);
  assert.equal(block.hasAttribute("data-editing"), true);
  await toggleView(block);
  assert.equal(selectedBlock(document), code.pos);
  assert.equal(block.hasAttribute("data-editing"), false);
  assert.ok(block.classList.contains("ProseMirror-selectednode"));

  await select(document, 1);
  await select(document, code.start + 1);
  assert.equal(selectedBlock(document), code.pos);
  assert.equal(block.hasAttribute("data-editing"), false);
});

test("arrow keys select a Diagram only block as a whole and then leave it", async () => {
  const { document, container } = await mount("whole-arrows.md", SOURCE);
  const block = blockElement(container);
  await rendered(block, "Start");
  await toggleView(block);
  const mermaid = codeBlockAt(document);
  const beforeEnd = 1 + "Before.".length;
  const afterStart = mermaid.pos + mermaid.size + 1;

  const cases: { from: number; keys: [string, string]; to: number }[] = [
    { from: beforeEnd, keys: ["ArrowRight", "ArrowRight"], to: afterStart },
    { from: beforeEnd, keys: ["ArrowDown", "ArrowDown"], to: afterStart },
    { from: afterStart, keys: ["ArrowLeft", "ArrowLeft"], to: beforeEnd },
    { from: afterStart, keys: ["ArrowUp", "ArrowUp"], to: beforeEnd },
  ];
  for (const { from, keys, to } of cases) {
    await select(document, from);
    assert.equal(
      (await press(document, keys[0])).defaultPrevented,
      true,
      `${keys[0]} from ${from}`,
    );
    assert.equal(selectedBlock(document), mermaid.pos, `${keys[0]} from ${from}`);
    assert.equal(
      (await press(document, keys[1])).defaultPrevented,
      true,
      `${keys[1]} after ${from}`,
    );
    assert.equal(selectedBlock(document), null);
    assert.equal(document.editor.state.selection.head, to, `${keys[1]} after ${from}`);
  }
});

test("Backspace and Delete next to a Diagram only block select it without merging text into its source", async () => {
  const { document, container } = await mount("whole-delete.md", SOURCE);
  const block = blockElement(container);
  await rendered(block, "Start");
  await toggleView(block);
  const original = document.editor.state.doc;
  const mermaid = codeBlockAt(document);
  const afterStart = mermaid.pos + mermaid.size + 1;

  await select(document, afterStart);
  await press(document, "Backspace");
  assert.equal(selectedBlock(document), mermaid.pos);
  assert.ok(document.editor.state.doc.eq(original));

  await select(document, 1 + "Before.".length);
  await press(document, "Delete");
  assert.equal(selectedBlock(document), mermaid.pos);
  assert.ok(document.editor.state.doc.eq(original));

  // 代码块之后的空 paragraph 中按 Backspace、之前的空 paragraph 中按 Delete：删除这个 paragraph，整体选中代码块，代码块
  // 仍为 "Diagram only" 视图。Backspace in an empty paragraph after the block and Delete in one before it delete the
  // paragraph and select the block, which stays in the Diagram only view.
  await select(document, afterStart);
  await press(document, "Enter");
  assert.equal(document.editor.state.doc.child(2).content.size, 0);
  await select(document, afterStart);
  await press(document, "Backspace");
  assert.equal(selectedBlock(document), mermaid.pos);
  assert.ok(document.editor.state.doc.eq(original));
  assert.equal(block.dataset.view, "diagram");

  await select(document, 1 + "Before.".length);
  await press(document, "Enter");
  // "Before." 占 0–9，空 paragraph 的内容位置为 10。"Before." spans 0–9; the empty paragraph's content is at 10.
  const empty = 1 + "Before.".length + 2;
  assert.equal(document.editor.state.doc.child(1).content.size, 0);
  await select(document, empty);
  await press(document, "Delete");
  assert.equal(selectedBlock(document), mermaid.pos);
  assert.ok(document.editor.state.doc.eq(original));
  assert.equal(blockElement(container).dataset.view, "diagram");
});

test("the corner button asks the host for a preview in either view, and so does a click on a Diagram only diagram", async () => {
  window.document.documentElement.style.setProperty("--background", "rgb(255, 255, 255)");
  const { document, container } = await mount("preview.md", SOURCE);
  const block = blockElement(container);
  await rendered(block, "Start");
  await select(document, 1);
  host.previewImages.length = 0;

  // 默认视图：按钮请求预览，光标不进入源码。The default view: the button requests a preview without entering the source.
  const press = await clickOn(block.querySelector(".mermaid-diagram-open")!);
  assert.equal(press.defaultPrevented, true);
  assert.equal(document.editor.state.selection.head, 1);
  assert.equal(block.hasAttribute("data-editing"), false);
  await act(() => until(() => host.previewImages.length === 1, "the first preview request"));

  // 默认视图中点击图表进入源码，不请求预览。In the default view a diagram click enters the source, without a preview.
  await clickOn(block.querySelector(".mermaid-diagram > svg")!);
  assert.equal(block.hasAttribute("data-editing"), true);
  await settle();
  assert.equal(host.previewImages.length, 1);

  // "Diagram only" 视图：点击图表请求预览，选区不变。The Diagram only view: a diagram click requests a preview and
  // keeps the selection.
  await select(document, 1);
  await toggleView(block);
  await select(document, 1);
  const diagramPress = await clickOn(block.querySelector(".mermaid-diagram > svg")!);
  assert.equal(diagramPress.defaultPrevented, true);
  assert.equal(document.editor.state.selection.head, 1);
  await act(() => until(() => host.previewImages.length === 2, "the second preview request"));
  for (const image of host.previewImages) {
    assert.equal(image.type, "image/svg+xml");
    assert.match(new TextDecoder().decode(image.data), /Start[\s\S]*End/);
  }
});

test("loading the file again returns Mermaid blocks to the default view", async () => {
  const { document, container } = await mount("reload.md", SOURCE);
  await rendered(blockElement(container), "Start");
  await toggleView(blockElement(container));
  assert.equal(blockElement(container).dataset.view, "diagram");

  host.files.set("reload.md", SOURCE.replace("Start", "Reloaded"));
  await act(() => document.session.reload());
  await rendered(blockElement(container), "Reloaded");
  assert.equal(blockElement(container).dataset.view, undefined);
});
