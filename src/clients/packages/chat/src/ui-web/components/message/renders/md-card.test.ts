import assert from "node:assert/strict";
import { readFileSync } from "node:fs";
import test from "node:test";
import React from "react";
import { renderToStaticMarkup } from "react-dom/server";

const globalsCss = readFileSync(
  new URL("../../../../../../components/src/ui-tokens/tokens.css", import.meta.url),
  "utf8",
);
const mdCardSource = readFileSync(new URL("./md-card.tsx", import.meta.url), "utf8");
const reasoningSource = readFileSync(new URL("./reasoning.tsx", import.meta.url), "utf8");
const mathCss = readFileSync(new URL("../../../../math.css", import.meta.url), "utf8");

async function renderMdCard(mdText: string, enableMath?: boolean): Promise<string> {
  const { default: MdCard } = await import("./md-card.tsx");
  return renderToStaticMarkup(React.createElement(MdCard, { mdText, enableMath }));
}

function getRuleBody(selector: string): string {
  const match = globalsCss.match(new RegExp(`${selector.replaceAll(".", "\\.")}\\s*\\{([^}]*)\\}`));
  assert.ok(match, `${selector} rule should exist`);
  return match[1];
}

test("markdown unordered lists use outside disc markers", () => {
  const body = getRuleBody(".msg-content-md-ul");

  assert.match(body, /list-disc/);
  assert.match(body, /list-outside/);
  assert.doesNotMatch(body, /list-decimal/);
  assert.doesNotMatch(body, /list-inside/);
});

test("markdown ordered lists use outside decimal markers", () => {
  const body = getRuleBody(".msg-content-md-ol");

  assert.match(body, /list-decimal/);
  assert.match(body, /list-outside/);
  assert.doesNotMatch(body, /list-disc/);
  assert.doesNotMatch(body, /list-inside/);
});

test("markdown list items render paragraph-aware classes", () => {
  assert.match(mdCardSource, /li: \(\{ children \}\) => <li className="msg-content-md-li">/);
  assert.match(mdCardSource, /p: \(\{ children \}\) => <p className="msg-content-md-p">/);
});

test("markdown list item first paragraphs stay on the marker line", () => {
  const body = getRuleBody(".msg-content-md-li > .msg-content-md-p:first-child");

  assert.match(body, /inline/);
  assert.doesNotMatch(body, /block/);
});

test("markdown code uses the chat code background", () => {
  const body = getRuleBody(".msg-content-md-code");

  assert.match(body, /bg-\[#f4f4f4\]/);
  assert.match(body, /rounded/);
});

test("markdown code blocks use a header and one visible background layer", () => {
  const blockBody = getRuleBody(".msg-content-md-code-block");
  const headerBody = getRuleBody(".msg-content-md-code-header");
  const preBody = getRuleBody("pre.msg-content-md-code-block-body");
  const nestedCodeBody = getRuleBody(
    "pre.msg-content-md-code-block-body > code.msg-content-md-code",
  );

  assert.match(blockBody, /overflow-hidden/);
  assert.match(headerBody, /border-b/);
  assert.match(preBody, /overflow-x-auto/);
  assert.match(preBody, /p-4/);
  assert.match(nestedCodeBody, /bg-transparent/);
  assert.match(nestedCodeBody, /p-0/);
  assert.match(mdCardSource, /<MarkdownCodeBlock>\{children\}<\/MarkdownCodeBlock>/);
});

test("fenced markdown code shows its language and block actions", async () => {
  const fence = String.fromCharCode(96).repeat(3);
  const html = await renderMdCard(
    [fence + "typescript", "const answer: number = 42;", fence].join("\n"),
  );

  assert.match(html, /msg-content-md-code-block/);
  assert.match(html, />typescript<\/span>/);
  assert.match(html, /aria-label="Disable word wrap"/);
  assert.match(html, /aria-pressed="true"/);
  assert.match(html, /aria-label="Copy code"/);
});

test("fenced markdown code without a language falls back to plain", async () => {
  const fence = String.fromCharCode(96).repeat(3);
  const html = await renderMdCard([fence, "agentName = claude-code", fence].join("\n"));

  assert.match(html, />plain<\/span>/);
});

test("indented code blocks render plainly without the block header", async () => {
  const html = await renderMdCard(["some text", "", "    const answer = 42;", ""].join("\n"));

  assert.match(html, /<pre class="msg-content-md-code overflow-x-auto agw-scrollbar">/);
  assert.doesNotMatch(html, /msg-content-md-code-block/);
  assert.doesNotMatch(html, /Copy code|word wrap/);
});

test("unfenced diff text does not produce per-line code block headers", async () => {
  const html = await renderMdCard(
    [
      "+function getTextContent(node: React.ReactNode): string {",
      "- return React.Children.toArray(node)",
      "",
      "      return String(child);",
    ].join("\n"),
  );

  assert.match(html, /<pre class="msg-content-md-code/);
  assert.doesNotMatch(html, /msg-content-md-code-block/);
});

test("indented code blocks keep plain pre styling", () => {
  const preBody = getRuleBody("pre.msg-content-md-code");
  const nestedCodeBody = getRuleBody("pre.msg-content-md-code > code.msg-content-md-code");

  assert.match(preBody, /p-3/);
  assert.match(nestedCodeBody, /bg-transparent/);
  assert.match(nestedCodeBody, /p-0/);
});

test("inline markdown code keeps the inline renderer without block controls", async () => {
  const html = await renderMdCard("Use `agentName` in this sentence.");

  assert.match(html, /<code class="msg-content-md-code">agentName<\/code>/);
  assert.doesNotMatch(html, /msg-content-md-code-block/);
  assert.doesNotMatch(html, /Copy code|word wrap/);
});

test("markdown code blocks toggle wrapping and copy only their code text", () => {
  assert.match(mdCardSource, /data-wrap=\{isWrapped\}/);
  assert.match(mdCardSource, /setIsWrapped\(\(current\) => !current\)/);
  assert.match(mdCardSource, /navigator\.clipboard\.writeText\(code\)/);
  assert.match(mdCardSource, /aria-label=\{copyLabel\}/);
  assert.match(mdCardSource, /<Copy/);
  assert.match(mdCardSource, /<Check/);
});

test("markdown lists collapse parser whitespace between list items and paragraphs", () => {
  const orderedBody = getRuleBody(".msg-content-md-ol");
  const unorderedBody = getRuleBody(".msg-content-md-ul");
  const listItemBody = getRuleBody(".msg-content-md-li");

  assert.match(orderedBody, /whitespace-normal/);
  assert.match(unorderedBody, /whitespace-normal/);
  assert.match(listItemBody, /whitespace-normal/);
});

test("markdown tables use a bordered responsive data-table surface", () => {
  const wrapperBody = getRuleBody(".msg-content-md-table-wrap");
  const tableBody = getRuleBody(".msg-content-md-table");
  const headerBody = getRuleBody(".msg-content-md-table th");
  const cellBody = getRuleBody(".msg-content-md-table td");

  assert.match(mdCardSource, /msg-content-md-table-wrap agw-scrollbar/);
  assert.match(mdCardSource, /<table className="msg-content-md-table">/);
  assert.match(mdCardSource, /aria-label="Scrollable table"/);
  assert.match(wrapperBody, /overflow-x-auto/);
  assert.match(wrapperBody, /rounded-lg/);
  assert.match(wrapperBody, /border-border/);
  assert.match(tableBody, /min-w-\[36rem\]/);
  assert.match(tableBody, /whitespace-normal/);
  assert.match(headerBody, /px-3/);
  assert.match(headerBody, /py-2\.5/);
  assert.match(cellBody, /leading-relaxed/);
  assert.match(cellBody, /wrap-break-word/);
});

test("markdown math uses remark-math and KaTeX", () => {
  assert.match(
    mdCardSource,
    /remarkPlugins=\{enableMath \? \[remarkGfm, remarkMath\] : \[remarkGfm\]\}/,
  );
  assert.match(mdCardSource, /rehypePlugins=\{enableMath \? \[rehypeKatex\] : \[\]\}/);
  assert.match(mdCardSource, /normalizeMathDelimiters\(mdText\)/);
  assert.match(mathCss, /@import "katex\/dist\/katex\.min\.css"/);
});

test("reasoning uses the shared Markdown renderer with math support", () => {
  assert.match(reasoningSource, /<MdCard mdText=\{expanded \? node\.content : preview\} \/>/);
});

test("markdown math remains enabled by default", async () => {
  const html = await renderMdCard(String.raw`公式：\(x + 1\)`);

  assert.match(html, /class="katex"/);
});

test("markdown links open outside the current application", async () => {
  const html = await renderMdCard("[Agw](https://github.com/zxyao145/agw)");

  assert.match(html, /href="https:\/\/github\.com\/zxyao145\/agw"/);
  assert.match(html, /target="_blank"/);
  assert.match(html, /rel="noreferrer"/);
  assert.match(html, /text-\[#2e82d2\]/);
  assert.match(html, /hover:underline/);
  assert.match(html, /lucide-external-link/);
});

test("non-web markdown links render as non-navigating file references", async () => {
  const html = await renderMdCard("[Chat.tsx](/Users/example/Chat.tsx:434)");

  assert.doesNotMatch(html, /href=/);
  assert.doesNotMatch(html, /target=/);
  assert.doesNotMatch(html, /lucide-external-link/);
  assert.match(html, /lucide-file-text[\s\S]*Chat\.tsx/);
});

test("reasoning text can render without KaTeX", async () => {
  const html = await renderMdCard("$PATH and \\alpha plus `\\beta`", false);

  assert.doesNotMatch(html, /katex/);
  assert.match(html, /\$PATH/);
  assert.match(html, /\\alpha/);
  assert.match(html, /\\beta/);
});

test("display math scrolls horizontally without clipping tall glyphs", () => {
  const displayBody = getRuleBody(".msg-content .katex-display");
  const formulaBody = getRuleBody(".msg-content .katex-display > .katex");

  assert.match(displayBody, /overflow-x-auto/);
  assert.match(displayBody, /py-1/);
  assert.doesNotMatch(displayBody, /overflow-y-hidden/);
  assert.match(formulaBody, /min-w-max/);
});
