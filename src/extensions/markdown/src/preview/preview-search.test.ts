import assert from "node:assert/strict";
import { test } from "node:test";

import { JSDOM } from "jsdom";
import { renderToStaticMarkup } from "react-dom/server";

import { EMPTY_QUERY, createSearchQuery, type FindQuery } from "../shared/find-query";

// 浏览器中有 window.DOMParser，Markdown 管线与浏览器走同一条 HTML 解析路径。
// Browsers have window.DOMParser, so the Markdown pipeline takes the browser's HTML parsing path.
const { window } = new JSDOM("<!doctype html><html><body></body></html>");
Object.assign(globalThis, { window, document: window.document, DOMParser: window.DOMParser });

const { renderMarkdown } = await import("./markdown-pipeline");
const { collectTextRuns, findTextMatches, matchRange } = await import("./preview-search");

function article(html: string): HTMLElement {
  const element = window.document.createElement("article");
  element.className = "markdown-body";
  element.innerHTML = html;
  return element;
}

function rendered(markdown: string): HTMLElement {
  return article(renderToStaticMarkup(renderMarkdown(markdown).element));
}

function query(search: string, options: Partial<FindQuery> = {}): FindQuery {
  return { ...EMPTY_QUERY, search, ...options };
}

/** 匹配的文字，经 Range 读取。The match texts, read through their Ranges. */
function matches(root: HTMLElement, find: FindQuery): string[] {
  const runs = collectTextRuns(root);
  return findTextMatches(runs, createSearchQuery(find)).map(({ run, from, to }) =>
    matchRange(runs[run]!, from, to).toString(),
  );
}

test("text runs follow block elements, and inline elements do not split them", () => {
  const root = rendered(
    [
      "Some **bold** and `code` text.",
      "- tight item\n- second",
      "| a | b |\n| - | - |\n| c | d |",
      "```ts\nconst x = 1;\n```",
      "> Quoted *line*.",
    ].join("\n\n"),
  );
  const runs = collectTextRuns(root);
  assert.deepEqual(
    runs.map((run) => run.text),
    [
      "Some bold and code text.",
      "tight item",
      "second",
      "a",
      "b",
      "c",
      "d",
      "const x = 1;\n",
      "Quoted line.",
    ],
  );
  assert.equal(runs[0]!.nodes.length, 5);
});

test("hidden text stays out: the footnote label, rendered diagram source and SVG", () => {
  const footnotes = rendered("Note[^1].\n\n[^1]: The note.");
  assert.ok(footnotes.querySelector(".sr-only"), "the footnote area has a visually hidden label");
  assert.deepEqual(matches(footnotes, query("Footnotes")), []);
  assert.deepEqual(matches(footnotes, query("note")), ["Note", "note"]);

  const diagrams = article(
    [
      '<div class="mermaid-block" data-diagram="rendered">',
      "<pre><code>graph cat</code></pre>",
      '<div class="mermaid-diagram"><svg><text>cat label</text></svg></div>',
      "</div>",
      '<div class="mermaid-block" data-diagram="failed">',
      "<pre><code>broken cat</code></pre>",
      '<div class="mermaid-diagram"><p class="mermaid-diagram-error">cat error</p></div>',
      "</div>",
    ].join(""),
  );
  assert.deepEqual(
    collectTextRuns(diagrams).map((run) => run.text),
    ["broken cat", "cat error"],
  );
});

test("matches cross inline elements but not blocks, and Ranges cover exactly the match", () => {
  const root = rendered("Some **bold** and `code` text.\n\nNext block.");
  assert.deepEqual(matches(root, query("bold and co")), ["bold and co"]);
  assert.deepEqual(matches(root, query("text. Next")), []);
  // 起点与终点都在节点交界处。Both ends on node boundaries.
  assert.deepEqual(matches(root, query("bold")), ["bold"]);
  const runs = collectTextRuns(root);
  const range = matchRange(runs[0]!, "Some ".length, "Some bold".length);
  assert.equal(range.startContainer.textContent, "bold");
  assert.equal(range.startOffset, 0);
  assert.equal(range.endContainer.textContent, "bold");
  assert.equal(range.endOffset, 4);
});

test("the options give the same matches as Mixed for the same document", () => {
  const root = rendered("Cat cat CAT category **cat**s concat.");
  assert.deepEqual(matches(root, query("cat")), ["Cat", "cat", "CAT", "cat", "cat", "cat"]);
  assert.deepEqual(matches(root, query("cat", { caseSensitive: true })), [
    "cat",
    "cat",
    "cat",
    "cat",
  ]);
  assert.deepEqual(matches(root, query("cat", { wholeWord: true })), ["Cat", "cat", "CAT"]);
  assert.deepEqual(matches(root, query("C[A-Z]T", { regexp: true, caseSensitive: true })), ["CAT"]);
  assert.equal(createSearchQuery(query("(", { regexp: true })).valid, false);
  assert.deepEqual(findTextMatches([], createSearchQuery(query("cat"))), []);
});
