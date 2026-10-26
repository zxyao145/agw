import assert from "node:assert/strict";
import { after, test } from "node:test";

import type { JSONContent } from "@tiptap/core";

import {
  createEditHost,
  fileUri,
  paste,
  pressKey,
  settle,
  typeText,
  until,
} from "./test-edit-host";
import { canDuplicate, canTurnInto, canTurnIntoSelection } from "./block-commands";
import { codeLanguages } from "./code-languages";
import { EditDocument } from "./edit-document";
import { applyLink, removeLink } from "./link-commands";
import { filterSlashItems } from "./slash-items";

// 编辑器行为（设计第 1061 行与 5.7 节）：输入规则、粘贴、复选框、node view、Enter、引用装饰与 `/` 菜单的条件。`/` 菜单的
// 键盘操作与浮层在 edit-view.test.ts 中。
// Editor behavior (design line 1061 and section 5.7): input rules, paste, the checkbox, node views, Enter, reference
// decorations and when the `/` menu opens. The `/` menu's keyboard handling and the popovers are in
// edit-view.test.ts.

const host = createEditHost();
const opened: EditDocument[] = [];
after(async () => {
  for (const document of opened) document.dispose();
  await settle();
  host.close();
});

async function open(text: string): Promise<EditDocument> {
  const name = `behavior-${opened.length}.md`;
  host.files.set(name, text);
  const document = new EditDocument(host.api, { uri: fileUri(name), name });
  opened.push(document);
  await until(() => document.session.state === "clean", `${name} to load`);
  return document;
}

/** 第一个文字等于 `text` 的文本块内容的开头位置。The start of the first textblock whose text is `text`. */
function startOf(document: EditDocument, text: string): number {
  let found: number | null = null;
  document.editor.state.doc.descendants((node, pos) => {
    if (found === null && node.isTextblock && node.textContent === text) found = pos + 1;
    return found === null;
  });
  assert.notEqual(found, null, `No textblock reads "${text}".`);
  return found!;
}

function listItems(document: EditDocument): { text: string; checked: unknown }[] {
  const items: { text: string; checked: unknown }[] = [];
  document.editor.state.doc.descendants((node) => {
    if (node.type.name === "listItem") {
      items.push({ text: node.firstChild?.textContent ?? "", checked: node.attrs.checked });
    }
    return true;
  });
  return items;
}

test("typing [ ] or [x] at the start of a list item turns it into a task item, elsewhere it stays text", async () => {
  const document = await open("- first\n- second\n\nPlain paragraph.\n");
  const { editor } = document;
  editor.commands.setTextSelection(startOf(document, "first"));
  typeText(editor, "[ ] ");
  editor.commands.setTextSelection(startOf(document, "second"));
  typeText(editor, "[x] ");
  assert.deepEqual(listItems(document), [
    { text: "first", checked: false },
    { text: "second", checked: true },
  ]);
  editor.commands.setTextSelection(startOf(document, "Plain paragraph."));
  typeText(editor, "[ ] ");
  assert.equal(editor.state.doc.lastChild?.textContent, "[ ] Plain paragraph.");
});

test("typing [^label] becomes a footnote reference only when the document defines that footnote", async () => {
  const document = await open("Text here.\n\n[^note]: The note.\n");
  const { editor } = document;
  const end = startOf(document, "Text here.") + "Text here.".length;
  editor.commands.setTextSelection(end);
  typeText(editor, " [^note]");
  typeText(editor, " [^missing]");
  const paragraph = editor.state.doc.firstChild!;
  const references: string[] = [];
  paragraph.descendants((node) => {
    if (node.type.name === "footnoteReference") references.push(String(node.attrs.label));
  });
  assert.deepEqual(references, ["note"]);
  assert.equal(paragraph.textContent, "Text here.  [^missing]");
});

test("pasting only text/plain inserts parsed Markdown, and inside a code block inserts the raw text", async () => {
  const document = await open("Start\n\n```\ncode\n```\n");
  const { editor } = document;
  editor.commands.setTextSelection(startOf(document, "Start") + "Start".length);
  paste(editor, { "text/plain": "**bold** and more\n\n- item one\n- item two" });
  const json: JSONContent = editor.getJSON();
  assert.deepEqual(json.content?.[0], {
    type: "paragraph",
    content: [
      { type: "text", text: "Start" },
      { type: "text", text: "bold", marks: [{ type: "bold" }] },
      { type: "text", text: " and more" },
    ],
  });
  assert.equal(json.content?.[1]?.type, "bulletList");
  assert.deepEqual(
    json.content?.[1]?.content?.map((item) => item.content?.[0]?.content?.[0]?.text),
    ["item one", "item two"],
  );

  editor.commands.setTextSelection(startOf(document, "code") + "code".length);
  paste(editor, { "text/plain": " **raw** - text" });
  let code = "";
  editor.state.doc.descendants((node) => {
    if (node.type.name === "codeBlock") code = node.textContent;
  });
  assert.equal(code, "code **raw** - text");
});

test("clicking a task item's checkbox toggles checked", async () => {
  const document = await open("- [ ] open task\n- [x] done task\n");
  const boxes = [
    ...document.editor.view.dom.querySelectorAll<HTMLInputElement>("input[type=checkbox]"),
  ];
  assert.deepEqual(
    boxes.map((box) => box.checked),
    [false, true],
  );
  boxes[0].click();
  assert.deepEqual(listItems(document), [
    { text: "open task", checked: true },
    { text: "done task", checked: true },
  ]);
  assert.equal(document.session.state, "dirty");
  boxes[1].click();
  assert.deepEqual(
    listItems(document).map((item) => item.checked),
    [true, false],
  );
});

test("raw blocks and front matter show their kind as a label", async () => {
  const document = await open(
    "---\ntitle: x\n---\n\n<div>html</div>\n\n[site]: https://example.com\n\nText[^n].\n\n[^n]: The note.\n",
  );
  const labels = [...document.editor.view.dom.querySelectorAll(".raw-block-label")].map(
    (label) => label.textContent,
  );
  assert.deepEqual(labels, ["Front matter", "HTML", "Link definition", "Footnote"]);
  assert.equal(document.editor.view.dom.querySelector(".footnote-reference")?.textContent, "[^n]");
});

test("images follow Preview's rules: project files through Blob URLs, data: directly, others as External image", async () => {
  host.files.set("images/logo.png", new Uint8Array([137, 80, 78, 71]));
  const document = await open(
    "![logo](images/logo.png) ![dot](data:image/png;base64,AA==) ![remote](https://example.com/x.png)\n",
  );
  const images = () => [...document.editor.view.dom.querySelectorAll(".edit-image")];
  await until(
    () => images()[0].querySelector("img")?.getAttribute("src")?.startsWith("blob:") === true,
    "the project image",
  );
  assert.equal(images()[0].querySelector("img")?.alt, "logo");
  assert.equal(images()[1].querySelector("img")?.getAttribute("src"), "data:image/png;base64,AA==");
  assert.equal(images()[2].querySelector("img"), null);
  assert.equal(images()[2].textContent, "remoteExternal image");
  assert.equal(
    images()[2].querySelector<HTMLElement>(".image-placeholder")?.title,
    "https://example.com/x.png",
  );
});

test("the block menu offers neither Duplicate nor Turn into for front matter", async () => {
  const document = await open("---\ntitle: x\n---\n\nBody.\n");
  const [frontMatter, body] = [
    document.editor.state.doc.child(0),
    document.editor.state.doc.child(1),
  ];
  assert.equal(frontMatter.type.name, "frontMatter");
  assert.deepEqual([canDuplicate(frontMatter), canTurnInto(frontMatter)], [false, false]);
  assert.deepEqual([canDuplicate(body), canTurnInto(body)], [true, true]);
});

test("the language list follows the languages it is given", () => {
  assert.deepEqual(
    codeLanguages(["python", "plaintext"]).map((language) => [language.name, language.label]),
    [["python", "Python"]],
  );
  assert.deepEqual(
    codeLanguages(["typescript", "javascript"]).map((language) => language.label),
    ["JavaScript", "TypeScript"],
  );
});

test("Enter that cannot split a list item leaves task items alone and makes a single transaction", async () => {
  const document = await open("- [x] done\n\n  > quoted\n\nPlain.\n");
  const { editor } = document;
  let transactions = 0;
  editor.on("transaction", () => {
    transactions += 1;
  });
  // 引用中的 Enter 分开所在的 paragraph，外层的任务项保持勾选。Enter in the quote splits its paragraph; the task item
  // around it stays checked.
  editor.commands.setTextSelection(startOf(document, "quoted") + "quoted".length);
  transactions = 0;
  pressKey(editor, "Enter");
  assert.deepEqual(
    listItems(document).map((item) => item.checked),
    [true],
  );
  assert.equal(transactions, 1);
  editor.commands.setTextSelection(startOf(document, "Plain.") + "Plain.".length);
  transactions = 0;
  pressKey(editor, "Enter");
  assert.equal(transactions, 1);
  assert.equal(editor.state.doc.lastChild?.content.size, 0);
});

test("a reference link that lost its definition drops the missing style once Apply or Remove changes it", async () => {
  for (const action of ["apply", "remove"] as const) {
    const document = await open("See [the docs][ref] now.\n\n[ref]: /old\n");
    const { editor } = document;
    let definition: { from: number; to: number } | null = null;
    editor.state.doc.forEach((node, offset) => {
      if (node.type.name === "rawBlock") definition = { from: offset, to: offset + node.nodeSize };
    });
    editor.commands.deleteRange(definition!);
    const missing = () => editor.view.dom.querySelectorAll(".missing-link-definition").length;
    assert.equal(missing(), 1);
    editor.commands.setTextSelection(startOf(document, "See the docs now.") + 6);
    if (action === "apply") applyLink(editor, "/new");
    else removeLink(editor);
    assert.equal(missing(), 0, action);
  }
});

/** 在 `text` 文本块的 `offset` 处输入，返回 `/` 菜单是否打开。Types at `offset` of the `text` textblock and returns
 *  whether the `/` menu opened. */
async function slashOpensAt(source: string, text: string, offset: number, typed: string) {
  const document = await open(source);
  document.editor.commands.setTextSelection(startOf(document, text) + offset);
  typeText(document.editor, typed);
  return document.slashMenu.getSnapshot() !== null;
}

test("the / menu opens only in an empty paragraph or after a space", async () => {
  assert.equal(await slashOpensAt("Hello there.\n", "Hello there.", 0, "/"), false);
  assert.equal(await slashOpensAt("Hello there.\n", "Hello there.", 5, "/"), false);
  assert.equal(await slashOpensAt("Hello there.\n", "Hello there.", 12, " /"), true);
  assert.equal(await slashOpensAt("# Title\n", "Title", 0, "/"), false);
  assert.equal(await slashOpensAt("First.\n\n#\n", "", 0, "/"), false);
  const document = await open("First.\n");
  document.editor.commands.setTextSelection(startOf(document, "First.") + "First.".length);
  pressKey(document.editor, "Enter");
  typeText(document.editor, "/");
  assert.notEqual(document.slashMenu.getSnapshot(), null);
});

test("in a table cell the / menu lists only items that apply there, and Turn into is unavailable", async () => {
  const document = await open("Intro.\n\n| a | b |\n| --- | --- |\n| 1 | 2 |\n");
  const { editor } = document;
  editor.commands.setTextSelection(startOf(document, "Intro.") + 1);
  assert.equal(filterSlashItems("", editor.state).length, 13);
  assert.equal(canTurnIntoSelection(editor.state), true);
  editor.commands.setTextSelection(startOf(document, "1") + 1);
  typeText(editor, " /");
  await until(
    () => document.slashMenu.getSnapshot()?.items.length === 1,
    "the / menu in the cell to list one item",
  );
  assert.deepEqual(
    filterSlashItems("", editor.state).map((item) => item.title),
    ["Image"],
  );
  assert.equal(canTurnIntoSelection(editor.state), false);
});
