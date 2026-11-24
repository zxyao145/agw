import assert from "node:assert/strict";
import { after, test } from "node:test";

import { getMatchHighlights } from "prosemirror-search";

import { EMPTY_QUERY, type FindQuery } from "../shared/find-query";
import { createEditHost, fileUri, settle, until } from "./test-edit-host";
import { EditDocument } from "./edit-document";
import { setMermaidDiagramOnly } from "./mermaid-editing";

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

function query(search: string, options: Partial<FindQuery> = {}): FindQuery {
  return { ...EMPTY_QUERY, search, ...options };
}

function decorations(document: EditDocument) {
  return getMatchHighlights(document.editor.state)
    .find()
    .sort((a, b) => a.from - b.from);
}

/** 高亮的匹配文字。The highlighted match texts. */
function matches(document: EditDocument): string[] {
  const { doc } = document.editor.state;
  return decorations(document).map(({ from, to }) => doc.textBetween(from, to));
}

/** 每个匹配所在的顶层块。The top-level block of every match. */
function matchBlocks(document: EditDocument): string[] {
  const { doc } = document.editor.state;
  return decorations(document).map(({ from }) => doc.resolve(from).node(1).type.name);
}

function selection(document: EditDocument): string {
  const { doc, selection } = document.editor.state;
  return doc.textBetween(selection.from, selection.to);
}

/** 第一个文字等于 `text` 的 textblock 内容的开头。The content start of the first textblock reading `text`. */
function startOf(document: EditDocument, text: string): number {
  let found: number | null = null;
  document.editor.state.doc.descendants((node, pos) => {
    if (found === null && node.isTextblock && node.textContent === text) found = pos + 1;
    return found === null;
  });
  assert.notEqual(found, null, `No textblock reads "${text}".`);
  return found!;
}

test("a search selects the first match from the selection on, counts the matches, and wraps both ways", async () => {
  const document = await open("count.md", "One cat.\n\nTwo cat cat.\n\nThree cat.\n");
  const { search, editor } = document;
  editor.commands.setTextSelection(startOf(document, "Two cat cat."));
  search.setQuery(query("cat"));
  assert.deepEqual(search.getResult(), { status: "matches", count: 4, current: 1 });
  assert.equal(selection(document), "cat");
  search.findNext();
  search.findNext();
  assert.deepEqual(search.getResult(), { status: "matches", count: 4, current: 3 });
  search.findNext();
  assert.deepEqual(search.getResult(), { status: "matches", count: 4, current: 0 });
  search.findPrevious();
  assert.deepEqual(search.getResult(), { status: "matches", count: 4, current: 3 });

  // 选区离开匹配后没有当前匹配；只改替换文字时选区不变。Without a selected match there is no current one; changing only
  // the replace text keeps the selection.
  editor.commands.setTextSelection(1);
  assert.deepEqual(search.getResult(), { status: "matches", count: 4, current: null });
  search.setQuery(query("cat", { replace: "dog" }));
  assert.equal(editor.state.selection.from, 1);
  assert.equal(editor.state.selection.empty, true);

  search.setQuery(query("cat heading"));
  assert.deepEqual(search.getResult(), { status: "matches", count: 0, current: null });
  search.setQuery(null);
  assert.deepEqual(search.getResult(), { status: "idle" });
  assert.deepEqual(matches(document), []);
});

test("the options follow prosemirror-search, and whole words stay whole across marks", async () => {
  const document = await open("options.md", "Cat cat CAT category **cat**s concat.\n");
  const { search } = document;
  search.setQuery(query("cat"));
  assert.deepEqual(matches(document), ["Cat", "cat", "CAT", "cat", "cat", "cat"]);
  search.setQuery(query("cat", { caseSensitive: true }));
  assert.deepEqual(matches(document), ["cat", "cat", "cat", "cat"]);
  search.setQuery(query("cat", { wholeWord: true }));
  assert.deepEqual(matches(document), ["Cat", "cat", "CAT"]);
  search.setQuery(query("cat", { caseSensitive: true, wholeWord: true }));
  assert.deepEqual(matches(document), ["cat"]);
  search.setQuery(query("C[A-Z]T", { regexp: true, caseSensitive: true }));
  assert.deepEqual(matches(document), ["CAT"]);
  search.setQuery(query("(", { regexp: true }));
  assert.deepEqual(search.getResult(), { status: "invalid" });
  assert.deepEqual(matches(document), []);
});

const SCOPE = `---
title: cat
---

# cat heading

- cat item

| cat | b |
| --- | --- |
| x | cat |

[link](https://cat.example/) ![cat alt](cat.png)

\`\`\`ts
const cat = 1;
\`\`\`

<div>cat html</div>

\`\`\`mermaid
graph TD
  cat --> dog
\`\`\`
`;

test("the search covers text in every block kind, skips node attributes, and leaves out Diagram only source", async () => {
  const document = await open("scope.md", SCOPE);
  const { search, editor } = document;
  search.setQuery(query("cat"));
  assert.deepEqual(matchBlocks(document), [
    "frontMatter",
    "heading",
    "bulletList",
    "table",
    "table",
    "codeBlock",
    "rawBlock",
    "codeBlock",
  ]);

  let mermaid = -1;
  editor.state.doc.forEach((node, offset) => {
    if (node.type.name === "codeBlock" && node.attrs.language === "mermaid") mermaid = offset;
  });
  setMermaidDiagramOnly(editor.view, mermaid, true);
  assert.equal(matchBlocks(document).length, 7);
  assert.deepEqual(search.getResult(), { status: "matches", count: 7, current: 0 });
  setMermaidDiagramOnly(editor.view, mermaid, false);
  assert.equal(matchBlocks(document).length, 8);
});

test("Replace replaces the selected match and selects the next one, and keeps the marks", async () => {
  const document = await open("replace.md", "A cat and **a cat**.\n\nAnother cat.\n");
  const { search, editor } = document;
  search.setQuery(query("cat", { replace: "dog" }));
  assert.deepEqual(search.getResult(), { status: "matches", count: 3, current: 0 });
  search.replace.next();
  assert.equal(editor.state.doc.child(0).textContent, "A dog and a cat.");
  assert.deepEqual(search.getResult(), { status: "matches", count: 2, current: 0 });
  search.replace.next();
  const bold = editor.state.doc.child(0).child(1);
  assert.equal(bold.text, "a dog");
  assert.deepEqual(
    bold.marks.map((mark) => mark.type.name),
    ["bold"],
  );
  assert.deepEqual(search.getResult(), { status: "matches", count: 1, current: 0 });
  assert.equal(selection(document), "cat");
  editor.commands.undo();
  assert.equal(editor.state.doc.child(0).textContent, "A dog and a cat.");
});

test("Replace All replaces every match in one undo step and expands regular expression groups", async () => {
  const document = await open(
    "replace-all.md",
    "Mail alice@example and bob@test.\n\nThen carol@home.\n",
  );
  const { search, editor } = document;
  search.setQuery(query("(\\w+)@(\\w+)", { regexp: true, replace: "$2 at $1" }));
  assert.deepEqual(search.getResult(), { status: "matches", count: 3, current: 0 });
  search.replace.all();
  assert.deepEqual(
    [editor.state.doc.child(0).textContent, editor.state.doc.child(1).textContent],
    ["Mail example at alice and test at bob.", "Then home at carol."],
  );
  assert.deepEqual(search.getResult(), { status: "matches", count: 0, current: null });
  editor.commands.undo();
  assert.deepEqual(
    [editor.state.doc.child(0).textContent, editor.state.doc.child(1).textContent],
    ["Mail alice@example and bob@test.", "Then carol@home."],
  );
});

test("saving after Replace All writes Markdown that differs only where the matches were", async () => {
  host.writes.length = 0;
  const document = await open("write.md", "Keep *this* line.\n\nA cat and **a cat**.\n");
  const { search } = document;
  search.setQuery(query("cat", { replace: "dog" }));
  search.replace.all();
  assert.equal(document.session.state, "dirty");
  await document.session.save();
  assert.deepEqual(host.writes, [
    { name: "write.md", text: "Keep *this* line.\n\nA dog and **a dog**.\n" },
  ]);
});

test("the search conditions stay after a reload and a file switch, without making the session unsaved", async () => {
  const document = await open("restore.md", "One cat.\n");
  const { search } = document;
  search.setQuery(query("cat"));
  assert.deepEqual(search.getResult(), { status: "matches", count: 1, current: 0 });

  host.files.set("restore.md", "One cat, two cat.\n");
  await document.session.reload();
  assert.equal(document.session.state, "clean");
  assert.deepEqual(matches(document), ["cat", "cat"]);

  host.files.set("restore-other.md", "cat cat cat\n");
  await document.show({ uri: fileUri("restore-other.md"), name: "restore-other.md" });
  assert.equal(document.session.state, "clean");
  assert.deepEqual(matches(document), ["cat", "cat", "cat"]);
});

test("selectedText returns single-line selections only", async () => {
  const document = await open("selected.md", "First cat.\n\nSecond line.\n");
  const { search, editor } = document;
  editor.commands.setTextSelection({ from: 7, to: 10 });
  assert.equal(search.selectedText(), "cat");
  editor.commands.setTextSelection({ from: 7, to: 17 });
  assert.equal(search.selectedText(), null);
  editor.commands.setTextSelection(7);
  assert.equal(search.selectedText(), null);
});
