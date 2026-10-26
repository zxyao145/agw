import assert from "node:assert/strict";
import { after, test } from "node:test";

import { createEditHost, fileUri, settle, typeText, until } from "./test-edit-host";
import { EditDocument } from "./edit-document";

const host = createEditHost();
const opened: EditDocument[] = [];
after(async () => {
  for (const document of opened) document.dispose();
  await settle();
  host.close();
});

async function open(name: string, text: string): Promise<EditDocument> {
  host.files.set(name, text);
  const document = new EditDocument(host.api, { uri: fileUri(name), name });
  opened.push(document);
  await until(() => document.session.state !== "loading", `${name} to load`);
  return document;
}

/** 第一个文字等于 `text` 的文本块内容的末尾位置。The end of the first textblock whose text is `text`. */
function endOf(document: EditDocument, text: string): number {
  let found: number | null = null;
  document.editor.state.doc.descendants((node, pos) => {
    if (found === null && node.isTextblock && node.textContent === text) {
      found = pos + 1 + node.content.size;
    }
    return found === null;
  });
  assert.notEqual(found, null, `No textblock reads "${text}".`);
  return found!;
}

function typeAt(document: EditDocument, position: number, text: string): void {
  document.editor.commands.setTextSelection(position);
  typeText(document.editor, text);
}

/** 把第一个 `type` 节点的文字换成 `text`。Replaces the text of the first `type` node with `text`. */
function replaceBlockText(document: EditDocument, type: string, text: string): void {
  const { editor } = document;
  let range: { from: number; to: number } | null = null;
  editor.state.doc.descendants((node, pos) => {
    if (range === null && node.type.name === type) {
      range = { from: pos + 1, to: pos + 1 + node.content.size };
    }
    return range === null;
  });
  assert.ok(range, `No ${type} node.`);
  const { from, to } = range as { from: number; to: number };
  editor.view.dispatch(editor.state.tr.insertText(text, from, to));
}

test("saving without edits writes nothing, also when a trailing paragraph was added after a final code block", async () => {
  const source = "# Title\n\nSome *text*.\n\n```ts\nconst a = 1;\n```\n";
  const document = await open("unchanged.md", source);
  assert.equal(document.session.state, "clean");
  // TrailingNode 在最后的代码块之后加入空 paragraph。TrailingNode adds an empty paragraph after the final code block.
  assert.equal(document.editor.state.doc.lastChild?.type.name, "paragraph");
  await document.session.save();
  assert.equal(document.session.state, "clean");
  assert.deepEqual(host.writes, []);
});

test("editing one paragraph writes only that paragraph, and the trailing paragraph adds nothing", async () => {
  host.writes.length = 0;
  const source = "Intro *stays* as written.\n\nChange me.\n\n```ts\nconst a = 1;\n```\n";
  const document = await open("one-paragraph.md", source);
  typeAt(document, endOf(document, "Change me."), " Now");
  assert.equal(document.session.state, "dirty");
  await document.session.save();
  assert.equal(document.session.state, "clean");
  assert.deepEqual(host.writes, [
    {
      name: "one-paragraph.md",
      text: "Intro *stays* as written.\n\nChange me. Now\n\n```ts\nconst a = 1;\n```\n",
    },
  ]);
  assert.deepEqual(host.notifications, []);
});

test("editing the last block keeps the text after it when a trailing paragraph was added", async () => {
  host.writes.length = 0;
  const document = await open("trailing.md", "- a\n- b\n\n\n");
  // TrailingNode 在最后的列表之后加入空 paragraph。TrailingNode adds an empty paragraph after the final list.
  assert.equal(document.editor.state.doc.lastChild?.type.name, "paragraph");
  typeAt(document, endOf(document, "b"), "X");
  await document.session.save();
  assert.deepEqual(host.writes, [{ name: "trailing.md", text: "- a\n- bX\n\n\n" }]);
});

test("undoing back to the baseline clears the unsaved flag", async () => {
  const document = await open("undo.md", "First.\n\nSecond.\n");
  // 之前的测试发出的未保存报告先到达宿主。Unsaved reports sent by earlier tests reach the host first.
  await settle();
  host.unsaved.length = 0;
  typeAt(document, endOf(document, "First."), "!!");
  await settle();
  assert.equal(document.session.state, "dirty");
  document.editor.commands.undo();
  await settle();
  assert.equal(document.editor.state.doc.textContent, "First.Second.");
  assert.equal(document.session.state, "clean");
  assert.deepEqual(host.unsaved, [true, false]);
});

test("a second save composes against the text the first save wrote", async () => {
  host.writes.length = 0;
  const document = await open("twice.md", "Hello *world*.\n\nKeep *this*.\n");
  typeAt(document, endOf(document, "Hello world."), "!");
  await document.session.save();
  assert.equal(host.files.get("twice.md"), "Hello _world_.!\n\nKeep *this*.\n");
  // 撤销后文档与载入时相同，但磁盘上是第一次写入的文字：这个块按 5.5 节重新输出。
  // After the undo the document equals the loaded one, but the disk holds the first write: the block is written again
  // per section 5.5.
  document.editor.commands.undo();
  await settle();
  assert.equal(document.session.state, "dirty");
  await document.session.save();
  assert.equal(document.session.state, "clean");
  assert.equal(host.files.get("twice.md"), "Hello _world_.\n\nKeep *this*.\n");
  assert.deepEqual(host.notifications, []);
});

test("a document that cannot be written as Markdown fails to save with the design's message and keeps the changes", async () => {
  host.writes.length = 0;
  host.notifications.length = 0;
  const source = "Before.\n\n<div>x</div>\n\nAfter.\n";
  const document = await open("raw.md", source);
  replaceBlockText(document, "rawBlock", "plain words");
  await document.session.save();
  await settle();
  assert.equal(document.session.state, "dirty");
  assert.deepEqual(host.writes, []);
  assert.equal(host.files.get("raw.md"), source);
  assert.deepEqual(host.notifications, [
    {
      type: "error",
      message:
        "Could not save raw.md: the document can't be written as Markdown without changing it.",
    },
  ]);
});

test("front matter edited into invalid YAML fails to save with the YAML message and keeps the changes", async () => {
  host.writes.length = 0;
  host.notifications.length = 0;
  const source = "---\ntitle: a\n---\n\nBody.\n";
  const document = await open("front-edit.md", source);
  replaceBlockText(document, "frontMatter", "title: [a");
  await document.session.save();
  await settle();
  assert.equal(document.session.state, "dirty");
  assert.deepEqual(host.writes, []);
  assert.equal(host.files.get("front-edit.md"), source);
  assert.equal(host.notifications.length, 1);
  assert.match(
    host.notifications[0].message,
    /^Could not save front-edit\.md: Front matter isn't valid YAML: Flow sequence/,
  );
});

test("invalid front matter fails to load with the YAML message", async () => {
  const document = await open("front.md", "---\ntitle: [unclosed\n---\n\nBody.\n");
  assert.equal(document.session.state, "failed");
  assert.match(document.session.error?.message ?? "", /^Front matter isn't valid YAML: /);
});

test("loading again replaces the content and clears the undo history", async () => {
  const document = await open("reload.md", "Old text.\n");
  typeAt(document, endOf(document, "Old text."), " typed");
  document.editor.commands.undo();
  await settle();
  host.files.set("reload.md", "New text.\n");
  await document.session.show({ uri: fileUri("reload.md"), name: "reload.md" });
  assert.equal(document.editor.state.doc.textContent, "New text.");
  assert.equal(document.editor.can().undo(), false);
  assert.equal(document.editor.can().redo(), false);
  assert.equal(document.session.state, "clean");
});
