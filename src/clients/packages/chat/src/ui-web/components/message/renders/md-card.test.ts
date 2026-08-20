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

test("markdown code blocks keep one visible background layer", () => {
  const preBody = getRuleBody("pre.msg-content-md-code");
  const nestedCodeBody = getRuleBody("pre.msg-content-md-code > code.msg-content-md-code");

  assert.match(preBody, /p-3/);
  assert.match(nestedCodeBody, /bg-transparent/);
  assert.match(nestedCodeBody, /p-0/);
  assert.match(mdCardSource, /msg-content-md-code overflow-x-auto agw-scrollbar/);
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

test("reasoning disables KaTeX rendering", () => {
  assert.match(
    reasoningSource,
    /<MdCard mdText=\{expanded \? node\.content : preview\} enableMath=\{false\} \/>/,
  );
});

test("markdown math remains enabled by default", async () => {
  const html = await renderMdCard(String.raw`公式：\(x + 1\)`);

  assert.match(html, /class="katex"/);
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
