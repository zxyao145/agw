import assert from "node:assert/strict";
import { after, test } from "node:test";

import { EditDocument } from "./edit-document";
import { createEditHost, fileUri, settle, typeText, until } from "./test-edit-host";

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

function positions(document: EditDocument): number[] {
  return document.outline.headings.map((_, index) => document.outline.position(index));
}

function count(document: EditDocument) {
  const calls = { count: 0 };
  const unsubscribe = document.outline.subscribe(() => (calls.count += 1));
  return { calls, unsubscribe };
}

test("the outline lists level 1-4 headings, nested ones included, each at its heading node", async () => {
  const document = await open(
    "levels.md",
    "# A\n\n## B\n\n### C\n\n#### D\n\n##### E\n\n###### F\n\n> ## Quoted\n\n- item\n\n  ### Listed\n\n" +
      "Setext\n---\n\n```sh\n# comment\n```\n",
  );
  assert.deepEqual(document.outline.headings, [
    { level: 1, label: "A" },
    { level: 2, label: "B" },
    { level: 3, label: "C" },
    { level: 4, label: "D" },
    { level: 2, label: "Quoted" },
    { level: 3, label: "Listed" },
    { level: 2, label: "Setext" },
  ]);
  document.outline.headings.forEach(({ level }, index) => {
    const node = document.editor.state.doc.nodeAt(document.outline.position(index));
    assert.equal(node?.type.name, "heading");
    assert.equal(node?.attrs.level, level);
  });
  assert.throws(() => document.outline.position(document.outline.headings.length), RangeError);
});

test("typing before the first heading keeps the headings array, notifies nobody and moves the positions", async () => {
  const document = await open("typing.md", "Intro.\n\n## First\n\n## Second\n");
  const headings = document.outline.headings;
  const before = positions(document);
  const { calls, unsubscribe } = count(document);
  document.editor.commands.setTextSelection(endOf(document, "Intro."));
  typeText(document.editor, "abc");
  assert.equal(document.outline.headings, headings);
  assert.equal(calls.count, 0);
  assert.deepEqual(
    positions(document),
    before.map((position) => position + 3),
  );
  unsubscribe();
});

test("editing text, creating a heading, changing its level and deleting it each notify once", async () => {
  const document = await open("changes.md", "## First\n\nBody.\n");
  const { editor } = document;
  const { calls, unsubscribe } = count(document);

  editor.view.dispatch(editor.state.tr.insertText(" part", endOf(document, "First")));
  assert.deepEqual(document.outline.headings, [{ level: 2, label: "First part" }]);
  assert.equal(calls.count, 1);

  editor.chain().setTextSelection(endOf(document, "Body.")).setHeading({ level: 3 }).run();
  assert.deepEqual(document.outline.headings, [
    { level: 2, label: "First part" },
    { level: 3, label: "Body." },
  ]);
  assert.equal(calls.count, 2);

  editor.chain().setTextSelection(endOf(document, "First part")).setHeading({ level: 5 }).run();
  assert.deepEqual(document.outline.headings, [{ level: 3, label: "Body." }]);
  assert.equal(calls.count, 3);

  const at = document.outline.position(0);
  editor.view.dispatch(editor.state.tr.delete(at, at + editor.state.doc.nodeAt(at)!.nodeSize));
  assert.deepEqual(document.outline.headings, []);
  assert.equal(calls.count, 4);
  unsubscribe();
});

test("undo and redo bring the headings back", async () => {
  const document = await open("undo.md", "## First\n\nBody.\n");
  const { editor } = document;
  const { calls, unsubscribe } = count(document);
  const both = [
    { level: 2, label: "First" },
    { level: 3, label: "Body." },
  ];
  editor.chain().setTextSelection(endOf(document, "Body.")).setHeading({ level: 3 }).run();
  assert.deepEqual(document.outline.headings, both);
  editor.commands.undo();
  assert.deepEqual(document.outline.headings, [{ level: 2, label: "First" }]);
  editor.commands.redo();
  assert.deepEqual(document.outline.headings, both);
  assert.equal(calls.count, 3);
  unsubscribe();
});

test("headings with the same text are separate items at their own positions", async () => {
  const document = await open("same.md", "## Intro\n\nOne.\n\n## Intro\n\nTwo.\n");
  assert.deepEqual(document.outline.headings, [
    { level: 2, label: "Intro" },
    { level: 2, label: "Intro" },
  ]);
  const [first, second] = positions(document);
  assert.ok(first! < second!);
  assert.equal(document.editor.state.doc.nodeAt(second!)?.type.name, "heading");
});

test("an empty heading is not an item until text is typed in it", async () => {
  const document = await open("empty.md", "Text.\n");
  const { editor } = document;
  const { calls, unsubscribe } = count(document);
  editor
    .chain()
    .setTextSelection(endOf(document, "Text."))
    .splitBlock()
    .setHeading({ level: 2 })
    .run();
  assert.deepEqual(document.outline.headings, []);
  assert.equal(calls.count, 0);
  typeText(editor, "New");
  assert.deepEqual(document.outline.headings, [{ level: 2, label: "New" }]);
  assert.equal(calls.count, 3);
  unsubscribe();
});

test("inline HTML keeps its source in the label and footnote definitions add no items", async () => {
  const document = await open(
    "raw.md",
    "## Install <kbd>npm</kbd>\n\nText[^a].\n\n[^a]: Note.\n\n    ## Inside note\n",
  );
  assert.deepEqual(document.outline.headings, [{ level: 2, label: "Install <kbd>npm</kbd>" }]);
});

test("a refresh that reads changed text updates the headings", async () => {
  const document = await open("refresh.md", "## Old\n");
  host.files.set("refresh.md", "## New\n\n### Added\n");
  await document.show({ uri: fileUri("refresh.md"), name: "refresh.md" });
  await until(() => document.outline.headings.length === 2, "the refreshed headings");
  assert.deepEqual(document.outline.headings, [
    { level: 2, label: "New" },
    { level: 3, label: "Added" },
  ]);
});

test("dispose stops notifications and a later unsubscribe still works", async () => {
  const document = await open("dispose.md", "## Title\n");
  const { calls, unsubscribe } = count(document);
  document.outline.dispose();
  document.editor.commands.setTextSelection(endOf(document, "Title"));
  typeText(document.editor, "s");
  assert.equal(calls.count, 0);
  unsubscribe();
});
