import assert from "node:assert/strict";
import { after, test } from "node:test";

import { NodeSelection } from "@tiptap/pm/state";

import { createEditHost, fileUri, pressKey, settle, typeText, until } from "./test-edit-host";
import { TURN_INTO_ITEMS, turnInto } from "./block-commands";
import { EditDocument } from "./edit-document";
import { insertImage, updateImage } from "./image-commands";
import { applyLink, removeLink } from "./link-commands";
import { SLASH_ITEMS } from "./slash-items";
import { TABLE_ACTIONS } from "./table-commands";

// 往返（设计第 1058 行）：编辑器能产生的每种结构执行后保存，两项校验都通过（会话保存成功，没有错误通知）。
// Round trips (design line 1058): every structure the editor can produce is saved with both verifications passing
// (the session saves and nothing is notified).

const host = createEditHost();
const opened: EditDocument[] = [];
after(async () => {
  for (const document of opened) document.dispose();
  await settle();
  host.close();
});

async function open(text: string): Promise<EditDocument> {
  const name = `round-trip-${opened.length}.md`;
  host.files.set(name, text);
  const document = new EditDocument(host.api, { uri: fileUri(name), name });
  opened.push(document);
  await until(() => document.session.state === "clean", `${name} to load`);
  return document;
}

/** 保存并返回磁盘上的文字；保存必须成功且没有通知。Saves and returns the disk text; the save must succeed silently. */
async function save(document: EditDocument): Promise<string> {
  host.notifications.length = 0;
  await document.session.save();
  assert.deepEqual(host.notifications, [], "the save notified an error");
  assert.equal(document.session.state, "clean");
  return host.files.get(document.session.file.name) as string;
}

/** 第一个文字等于 `text` 的文本块。The first textblock whose text is `text`. */
function block(document: EditDocument, text: string): { start: number; end: number; pos: number } {
  let found: { start: number; end: number; pos: number } | null = null;
  document.editor.state.doc.descendants((node, pos) => {
    if (found === null && node.isTextblock && node.textContent === text) {
      found = { pos, start: pos + 1, end: pos + 1 + node.content.size };
    }
    return found === null;
  });
  assert.notEqual(found, null, `No textblock reads "${text}".`);
  return found!;
}

const SLASH_OUTPUT: Record<string, string> = {
  Text: "Before.\n\ntyped\n\nAfter.\n",
  "Heading 1": "Before.\n\n# typed\n\nAfter.\n",
  "Heading 2": "Before.\n\n## typed\n\nAfter.\n",
  "Heading 3": "Before.\n\n### typed\n\nAfter.\n",
  "Bulleted list": "Before.\n\n- typed\n\nAfter.\n",
  "Numbered list": "Before.\n\n1. typed\n\nAfter.\n",
  "To-do list": "Before.\n\n- [ ] typed\n\nAfter.\n",
  Quote: "Before.\n\n> typed\n\nAfter.\n",
  "Code block": "Before.\n\n```\ntyped\n```\n\nAfter.\n",
  Table: "Before.\n\n|  |  |  |\n| --- | --- | --- |\n|  |  |  |\n|  |  |  |\n\nAfter.\n",
  Divider: "Before.\n\n---\n\nAfter.\n",
  Image: "Before.\n\n![](images/a.png)\n\nAfter.\n",
  "HTML block": "Before.\n\n<div>typed</div>\n\nAfter.\n",
};

test("every / menu item saves", async () => {
  assert.deepEqual(
    SLASH_ITEMS.map((item) => item.title),
    Object.keys(SLASH_OUTPUT),
  );
  for (const item of SLASH_ITEMS) {
    const document = await open("Before.\n\nAfter.\n");
    const { editor, ui } = document;
    editor.commands.setTextSelection(block(document, "Before.").end);
    pressKey(editor, "Enter");
    typeText(editor, "/");
    const { from } = editor.state.selection;
    const imageRequests: number[] = [];
    const subscription = ui.onImageInsert((position) => imageRequests.push(position));
    item.run(editor, { from: from - 1, to: from }, ui);
    subscription.dispose();
    if (item.title === "Image") {
      assert.equal(imageRequests.length, 1);
      insertImage(editor, imageRequests[0], "images/a.png");
    } else if (!["Table", "Divider"].includes(item.title)) {
      typeText(editor, "typed");
    }
    assert.equal(await save(document), SLASH_OUTPUT[item.title], item.title);
  }
});

const TURN_INTO_OUTPUT: Record<string, string> = {
  Text: "Para.\n\nTurn me.\n\nEnd.\n",
  "Heading 1": "Para.\n\n# Turn me.\n\nEnd.\n",
  "Heading 2": "Para.\n\n## Turn me.\n\nEnd.\n",
  "Heading 3": "Para.\n\n### Turn me.\n\nEnd.\n",
  "Bulleted list": "Para.\n\n- Turn me.\n\nEnd.\n",
  "Numbered list": "Para.\n\n1. Turn me.\n\nEnd.\n",
  "To-do list": "Para.\n\n- [ ] Turn me.\n\nEnd.\n",
  Quote: "Para.\n\n> Turn me.\n\nEnd.\n",
  "Code block": "Para.\n\n```\nTurn me.\n```\n\nEnd.\n",
};

// 列表项中：列表类型直接切换（整个列表），其他类型先移出列表，前后的列表项仍是两个列表。
// From a list item: list types switch in place (the whole list); other types leave the list first, and the items
// before and after stay two lists.
const TURN_INTO_FROM_ITEM: Record<string, string> = {
  Text: "- one\n\ntwo\n\n- three\n",
  "Heading 1": "- one\n\n# two\n\n- three\n",
  "Heading 2": "- one\n\n## two\n\n- three\n",
  "Heading 3": "- one\n\n### two\n\n- three\n",
  "Bulleted list": "- one\n- two\n- three\n",
  "Numbered list": "1. one\n2. two\n3. three\n",
  "To-do list": "- one\n- [ ] two\n- three\n",
  Quote: "- one\n\n> two\n\n- three\n",
  "Code block": "- one\n\n```\ntwo\n```\n\n- three\n",
};

test("every Turn into item saves, from a paragraph and from a list item", async () => {
  assert.deepEqual(
    TURN_INTO_ITEMS.map((item) => item.title),
    Object.keys(TURN_INTO_OUTPUT),
  );
  for (const item of TURN_INTO_ITEMS) {
    const fromParagraph = await open("Para.\n\nTurn me.\n\nEnd.\n");
    fromParagraph.editor.commands.setTextSelection(block(fromParagraph, "Turn me.").start + 2);
    turnInto(fromParagraph.editor, item.type);
    assert.equal(await save(fromParagraph), TURN_INTO_OUTPUT[item.title], item.title);

    const fromItem = await open("- one\n- two\n- three\n");
    fromItem.editor.commands.setTextSelection(block(fromItem, "two").start + 1);
    turnInto(fromItem.editor, item.type);
    assert.equal(await save(fromItem), TURN_INTO_FROM_ITEM[item.title], item.title);
  }
});

const TABLE = "Intro.\n\n| a | b |\n| :--- | ---: |\n| 1 | 2 |\n\nEnd.\n";

test("every table operation saves, with the column alignment kept", async () => {
  for (const action of TABLE_ACTIONS) {
    for (const cell of ["a", "1"]) {
      const document = await open(TABLE);
      document.editor.commands.setTextSelection(block(document, cell).end);
      assert.ok(action.run(document.editor), `${action.title} in cell ${cell}`);
      const written = await save(document);
      if (action.title.startsWith("Insert row")) {
        assert.match(written, /\| :--- \| ---: \|/, `${action.title} in cell ${cell}`);
      }
      if (action.title === "Delete table") assert.equal(written, "Intro.\n\nEnd.\n");
    }
  }
});

test("Shift+Enter in a table cell writes <br>", async () => {
  const document = await open(TABLE);
  document.editor.commands.setTextSelection(block(document, "1").end);
  pressKey(document.editor, "Enter", { shift: true });
  typeText(document.editor, "x");
  assert.equal(
    await save(document),
    "Intro.\n\n| a | b |\n| :--- | ---: |\n| 1<br>x | 2 |\n\nEnd.\n",
  );
});

test("toggling a task item and adding an empty task item save", async () => {
  const document = await open("- [ ] one\n- [ ] two\n");
  const boxes = document.editor.view.dom.querySelectorAll<HTMLInputElement>("input[type=checkbox]");
  boxes[1].click();
  assert.equal(await save(document), "- [ ] one\n- [x] two\n");
  document.editor.commands.setTextSelection(block(document, "two").end);
  pressKey(document.editor, "Enter");
  // 空任务项按规范化第 3 步写成文字 `[ ]`。An empty task item is written as the text `[ ]` (normalization step 3).
  assert.equal(await save(document), "- [ ] one\n- [x] two\n- \\[ ]\n");
});

test("links and images are added, edited and removed", async () => {
  const document = await open("Some words here.\n");
  const { editor } = document;
  const words = block(document, "Some words here.").start + "Some ".length;
  editor.commands.setTextSelection({ from: words, to: words + "words".length });
  applyLink(editor, "https://example.com");
  assert.equal(await save(document), "Some [words](https://example.com) here.\n");
  editor.commands.setTextSelection(words + 2);
  applyLink(editor, "docs/a.md");
  assert.equal(await save(document), "Some [words](docs/a.md) here.\n");
  editor.commands.setTextSelection(words + 2);
  removeLink(editor);
  assert.equal(await save(document), "Some words here.\n");

  const end = block(document, "Some words here.").end;
  insertImage(editor, end, "img/a.png");
  assert.equal(await save(document), "Some words here.![](img/a.png)\n");
  updateImage(editor, end, { src: "img/b.png", alt: "B" });
  assert.equal(await save(document), "Some words here.![B](img/b.png)\n");
  editor.commands.deleteRange({ from: end, to: end + 1 });
  assert.equal(await save(document), "Some words here.\n");
});

test("editing the address of a reference link turns it into a plain link", async () => {
  const document = await open("See [the docs][ref] now.\n\n[ref]: /old\n");
  document.editor.commands.setTextSelection(block(document, "See the docs now.").start + 6);
  applyLink(document.editor, "/new");
  assert.equal(await save(document), "See [the docs](/new) now.\n\n[ref]: /old\n");
});

test("dragging a block into a list item saves", async () => {
  const document = await open("- item one\n- item two\n\nMove me.\n");
  const { editor } = document;
  // ProseMirror 放下拖动的块时的事务：删除选中的块，把它的内容放到目标位置。
  // The transaction ProseMirror makes when a dragged block is dropped: delete the selected block, insert its content
  // at the target.
  const moved = block(document, "Move me.");
  const tr = editor.state.tr.setSelection(NodeSelection.create(editor.state.doc, moved.pos));
  const slice = tr.selection.content();
  const target = block(document, "item one").end + 1;
  tr.deleteSelection();
  tr.replaceRange(tr.mapping.map(target), tr.mapping.map(target), slice);
  editor.view.dispatch(tr);
  assert.equal(editor.state.doc.firstChild?.firstChild?.childCount, 2);
  const written = await save(document);
  assert.match(written, /^- item one\n\n {2}Move me\.\n/);
});

test("two adjacent bullet lists stay two lists", async () => {
  const document = await open("* a\n\nBetween.\n\n* b\n");
  const between = block(document, "Between.");
  document.editor.commands.deleteRange({ from: between.pos, to: between.end + 1 });
  assert.equal(await save(document), "* a\n\n- b\n");
});

function chooseLanguage(document: EditDocument, index: number, language: string): void {
  const select = document.editor.view.dom.querySelectorAll<HTMLSelectElement>("select")[index];
  // 选择框在获得焦点时列出全部语言。The select lists every language once it gets focus.
  select.dispatchEvent(new window.FocusEvent("focus"));
  select.value = language;
  assert.equal(select.value, language);
  select.dispatchEvent(new window.Event("change", { bubbles: true }));
}

test("choosing a code block language replaces the first word of the info", async () => {
  const document = await open("```ts title=x\nconst a = 1;\n```\n\n```ts\nlet b;\n```\n");
  chooseLanguage(document, 0, "javascript");
  assert.equal(
    await save(document),
    "```javascript title=x\nconst a = 1;\n```\n\n```ts\nlet b;\n```\n",
  );
  chooseLanguage(document, 1, "");
  assert.equal(
    await save(document),
    "```javascript title=x\nconst a = 1;\n```\n\n```\nlet b;\n```\n",
  );
  chooseLanguage(document, 0, "");
  assert.equal(await save(document), "```text title=x\nconst a = 1;\n```\n\n```\nlet b;\n```\n");
});
