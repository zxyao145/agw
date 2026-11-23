import assert from "node:assert/strict";
import test from "node:test";

import { JSDOM } from "jsdom";
import { createElement } from "react";
import { renderToStaticMarkup } from "react-dom/server";

import type { MermaidBlockProps } from "./markdown-pipeline";

// 浏览器中有 window.DOMParser，测试与浏览器走同一条 HTML 解析路径。
// Browsers have window.DOMParser, so the tests take the browser's HTML parsing path.
const { window } = new JSDOM("");
Object.assign(globalThis, { window, DOMParser: window.DOMParser });

const { indexRenderedHeadings, renderMarkdown } = await import("./markdown-pipeline");

function render(markdown: string): string {
  return renderToStaticMarkup(renderMarkdown(markdown).element);
}

const FENCE = "```";

test("headings get GitHub-style ids", () => {
  const html = render(
    "# Hello World\n\n## Hello World\n\n### 中文 标题\n\n#### API: `get()` & more!",
  );
  assert.match(html, /<h1 id="hello-world">/);
  assert.match(html, /<h2 id="hello-world-1">/);
  assert.match(html, /<h3 id="中文-标题">/);
  assert.match(html, /<h4 id="api-get--more">/);
});

test("raw HTML is shown as text and never rendered", () => {
  const html = render('<script>alert(1)</script>\n\n<b onclick="x()">bold</b>');
  assert.doesNotMatch(html, /<script|<b[ >]/);
  assert.match(html, /&lt;script&gt;alert\(1\)&lt;\/script&gt;/);
});

test("GFM tables, task lists, strikethrough and autolinks render", () => {
  const html = render(
    "| a | b |\n| - | - |\n| 1 | 2 |\n\n- [x] done\n- [ ] todo\n\n~~old~~ www.example.com",
  );
  assert.match(html, /<table>/);
  assert.match(html, /<td>1<\/td>/);
  assert.match(html, /<input type="checkbox" disabled="" checked=""\/> done/);
  assert.match(html, /<del>old<\/del>/);
  assert.match(html, /<a href="http:\/\/www\.example\.com">www\.example\.com<\/a>/);
});

test("fenced code with a language is highlighted", () => {
  const html = render("```ts\nconst answer = 42;\n```\n\n```\nplain\n```");
  assert.match(html, /<code class="hljs language-ts"><span class="hljs-keyword">const<\/span>/);
  assert.match(html, /<pre><code>plain\n<\/code><\/pre>/);
});

test("Mermaid code blocks stay code blocks without a mermaid component and go to it with one", () => {
  const markdown = "```mermaid\nflowchart LR\n```";
  assert.match(
    render(markdown),
    /<pre><code class="language-mermaid">flowchart LR\n<\/code><\/pre>/,
  );
  const figure = ({ source, children }: MermaidBlockProps) =>
    createElement("figure", { "data-source": source }, children);
  assert.equal(
    renderToStaticMarkup(renderMarkdown(markdown, { mermaid: figure }).element),
    '<figure data-source="flowchart LR"><pre><code class="language-mermaid">flowchart LR\n</code></pre></figure>',
  );
});

test("image sources stay unchanged while unsafe link URLs are removed", () => {
  const html = render(
    "![logo](../images/logo.png) ![dot](data:image/png;base64,AAAA) [x](javascript:alert(1))",
  );
  assert.match(html, /<img src="\.\.\/images\/logo\.png" alt="logo"\/>/);
  assert.match(html, /<img src="data:image\/png;base64,AAAA" alt="dot"\/>/);
  assert.match(html, /<a href="">x<\/a>/);
});

test("footnotes render the same DOM as remark", () => {
  const html = render(
    "A[^Note] b[^x] c[^Note].\n\n[^x]: X text.\n[^Note]: Note **text**.\n\n    more",
  );
  // remark-gfm 对同一输入的输出，去掉了元素之间的换行。remark-gfm's output for the same input, without newlines between elements.
  const remark = [
    '<p>A<sup><a href="#user-content-fn-note" id="user-content-fnref-note" data-footnote-ref="true" aria-describedby="footnote-label">1</a></sup>',
    ' b<sup><a href="#user-content-fn-x" id="user-content-fnref-x" data-footnote-ref="true" aria-describedby="footnote-label">2</a></sup>',
    ' c<sup><a href="#user-content-fn-note" id="user-content-fnref-note-2" data-footnote-ref="true" aria-describedby="footnote-label">1</a></sup>.</p>',
    '<section data-footnotes="true" class="footnotes"><h2 class="sr-only" id="footnote-label">Footnotes</h2><ol>',
    '<li id="user-content-fn-note"><p>Note <strong>text</strong>.</p>',
    '<p>more <a href="#user-content-fnref-note" data-footnote-backref="" aria-label="Back to reference 1" class="data-footnote-backref">↩</a>',
    ' <a href="#user-content-fnref-note-2" data-footnote-backref="" aria-label="Back to reference 1-2" class="data-footnote-backref">↩<sup>2</sup></a></p></li>',
    '<li id="user-content-fn-x"><p>X text. <a href="#user-content-fnref-x" data-footnote-backref="" aria-label="Back to reference 2" class="data-footnote-backref">↩</a></p></li>',
    "</ol></section>",
  ].join("");
  assert.equal(html, remark);
});

test("only loose list items wrap their content in paragraphs", () => {
  assert.equal(render("- a\n- b"), "<ul><li>a</li><li>b</li></ul>");
  assert.equal(render("- a\n\n- b"), "<ul><li><p>a</p></li><li><p>b</p></li></ul>");
});

test("task lists mark the list and the task items as remark-gfm does", () => {
  assert.equal(
    render("- [ ] a\n\n- [ ] b"),
    '<ul class="contains-task-list">' +
      '<li class="task-list-item"><p><input type="checkbox" disabled=""/> a</p></li>' +
      '<li class="task-list-item"><p><input type="checkbox" disabled=""/> b</p></li></ul>',
  );
  assert.equal(
    render("1. [x] a\n2. b"),
    '<ol class="contains-task-list">' +
      '<li class="task-list-item"><input type="checkbox" disabled="" checked=""/> a</li>' +
      "<li>b</li></ol>",
  );
});

test("tables split the head row, align cells and keep cell text bare", () => {
  assert.equal(
    render("| a | b |\n| :- | -: |\n| 1 | 2 |"),
    '<table><thead><tr><th style="text-align:left">a</th><th style="text-align:right">b</th></tr></thead>' +
      '<tbody><tr><td style="text-align:left">1</td><td style="text-align:right">2</td></tr></tbody></table>',
  );
});

test("indented fences, plus signs, inline images and blank lines render cleanly", () => {
  assert.equal(render("  " + FENCE + "\nx\n  " + FENCE), "<pre><code>x\n</code></pre>");
  assert.equal(render("C++ and C++"), "<p>C++ and C++</p>");
  assert.match(render("see ![i](i.png) here"), /<p>see <img src="i\.png" alt="i"\/> here<\/p>/);
  assert.equal(render("\n\na\n\n\n\nb\n\n"), "<p>a</p><p>b</p>");
});

test("python code keeps its indentation", () => {
  const html = render(FENCE + "py\nif ready:\n    run()\n" + FENCE);
  assert.match(html, /<span class="hljs-keyword">if<\/span> ready:\n {4}run\(\)\n<\/code>/);
});

test("text entities decode while HTML stays verbatim", () => {
  assert.equal(render("a &amp; b"), "<p>a &amp; b</p>");
  assert.equal(render("<div>x &amp; y</div>"), "<p>&lt;div&gt;x &amp;amp; y&lt;/div&gt;</p>");
});

test("code in an unregistered language is not highlighted", () => {
  assert.equal(
    render(FENCE + "nosuchlang\nx\n" + FENCE),
    '<pre><code class="language-nosuchlang">x\n</code></pre>',
  );
});

function outline(markdown: string) {
  return renderMarkdown(markdown).outline;
}

test("the outline lists the level 1-4 headings in document order", () => {
  assert.deepEqual(outline("# A\n\n## B\n\n### C\n\n#### D\n\n##### E\n\n###### F\n\n## G"), [
    { level: 1, label: "A", id: "a" },
    { level: 2, label: "B", id: "b" },
    { level: 3, label: "C", id: "c" },
    { level: 4, label: "D", id: "d" },
    { level: 2, label: "G", id: "g" },
  ]);
});

test("setext headings and headings in blockquotes and list items are outline items", () => {
  assert.deepEqual(outline("Title\n=====\n\nSub\n---\n\n> ## Quoted\n\n- item\n\n  ### Listed\n"), [
    { level: 1, label: "Title", id: "title" },
    { level: 2, label: "Sub", id: "sub" },
    { level: 2, label: "Quoted", id: "quoted" },
    { level: 3, label: "Listed", id: "listed" },
  ]);
});

test("outline labels keep the text of marks, drop images and footnote references and collapse spaces", () => {
  const labels = outline(
    "## **Bold** *it* ~~old~~ `code` [link](https://example.com) ![img](i.png) note[^n]\n\n" +
      "### Spaced   out\ttext\n\n[^n]: Note.\n",
  ).map(({ label }) => label);
  assert.deepEqual(labels, ["Bold it old code link note", "Spaced out text"]);
});

test("empty headings are not outline items", () => {
  assert.deepEqual(outline("#\n\n## Kept\n\n###   \n"), [{ level: 2, label: "Kept", id: "kept" }]);
});

test("outline ids match the rendered heading ids, with suffixes for repeated headings", () => {
  const { element, outline: items } = renderMarkdown("# Intro\n\n## Intro\n\n### 中文 标题");
  assert.deepEqual(
    items.map(({ id }) => id),
    ["intro", "intro-1", "中文-标题"],
  );
  const html = renderToStaticMarkup(element);
  for (const { id } of items) assert.match(html, new RegExp(`<h\\d id="${id}">`));
});

test("the footnotes section and headings in footnote definitions are not outline items", () => {
  const { element, outline: items } = renderMarkdown(
    "# Doc\n\nText[^a].\n\n[^a]: Note.\n\n    ## Inside note\n",
  );
  // 确认脚注定义渲染出了标题。Make sure the footnote definition renders a heading.
  assert.match(
    renderToStaticMarkup(element),
    /<li id="user-content-fn-a">.*<h2 id="inside-note">/su,
  );
  assert.deepEqual(items, [{ level: 1, label: "Doc", id: "doc" }]);
});

test("heading lookup prefers the document heading when the footnotes label shares its id", () => {
  const article = window.document.createElement("article");
  article.innerHTML = render("# Footnote label\n\nText[^a].\n\n[^a]: Note.\n");
  assert.equal(article.querySelectorAll('[id="footnote-label"]').length, 2);
  assert.equal(indexRenderedHeadings(article).get("footnote-label")?.textContent, "Footnote label");
});

test("lines starting with # inside code blocks are not outline items", () => {
  assert.deepEqual(
    outline(FENCE + "sh\n# comment\n" + FENCE + "\n\n    # indented code\n\n## Real\n"),
    [{ level: 2, label: "Real", id: "real" }],
  );
});

test("inline HTML in a heading stays in the outline label as written", () => {
  assert.deepEqual(
    outline("## Install <kbd>npm</kbd>").map(({ label }) => label),
    ["Install <kbd>npm</kbd>"],
  );
});
