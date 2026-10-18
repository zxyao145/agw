import assert from "node:assert/strict";
import test from "node:test";

import { createElement } from "react";
import { renderToStaticMarkup } from "react-dom/server";
import Markdown from "react-markdown";

import { rehypePlugins, remarkPlugins, urlTransform } from "./markdown-pipeline";

function render(markdown: string): string {
  return renderToStaticMarkup(
    createElement(Markdown, { remarkPlugins, rehypePlugins, urlTransform }, markdown),
  );
}

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

test("image sources stay unchanged while unsafe link URLs are removed", () => {
  const html = render(
    "![logo](../images/logo.png) ![dot](data:image/png;base64,AAAA) [x](javascript:alert(1))",
  );
  assert.match(html, /<img src="\.\.\/images\/logo\.png" alt="logo"\/>/);
  assert.match(html, /<img src="data:image\/png;base64,AAAA" alt="dot"\/>/);
  assert.match(html, /<a href="">x<\/a>/);
});
