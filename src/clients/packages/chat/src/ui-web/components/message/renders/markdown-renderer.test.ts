import assert from "node:assert/strict";
import test from "node:test";
import { setupDomEnvironment } from "@agw/test-harness";

const { React, fireEvent, render, screen } = await setupDomEnvironment();
const { default: MdCard } = await import("./md-card.tsx");
const { default: TextContent } = await import("./text-content.tsx");
const { default: Reasoning } = await import("./reasoning.tsx");
const { default: PlanCard } = await import("./plan-card.tsx");
const { MarkdownRendererProvider } = await import("./markdown-renderer.tsx");
const { TiptapMarkdown } = await import("../../../markdown-tiptap/index.ts");

const MARKDOWN = [
  "Check[^n] **bold** `code` [Agw](https://example.com)",
  "",
  "| a | b |",
  "| --- | --: |",
  "| 1 | 2 |",
  "",
  "[^n]: Footnote text.",
].join("\n");
const ONE_LINE = "Check **bold** `code` [Agw](https://example.com)";

function mdCardHtml(markdown: string): string {
  const view = render(React.createElement(MdCard, { markdown }));
  const html = view.container.innerHTML;
  view.unmount();
  return html;
}

test("without a provider, text content renders through MdCard", () => {
  const expected = mdCardHtml(MARKDOWN);
  const view = render(
    React.createElement(TextContent, { node: { type: "text", content: MARKDOWN } }),
  );

  assert.equal(view.container.querySelector(".msg-content")?.innerHTML, expected);
});

test("without a provider, reasoning renders through MdCard", () => {
  const expected = mdCardHtml(ONE_LINE);
  const view = render(
    React.createElement(Reasoning, { node: { type: "reasoning", content: ONE_LINE } }),
  );

  assert.equal(view.container.querySelector(".flex-1")?.innerHTML, expected);
});

test("without a provider, every plan part renders through MdCard", () => {
  const expected = mdCardHtml(MARKDOWN);
  const view = render(
    React.createElement(PlanCard, {
      leadingMarkdown: MARKDOWN,
      markdown: MARKDOWN,
      trailingMarkdown: MARKDOWN,
      isClosed: true,
    }),
  );

  const parts = [...view.container.querySelectorAll(":scope > .msg-content, section .msg-content")];
  assert.equal(parts.length, 3);
  for (const part of parts) assert.equal(part.innerHTML, expected);
});

function withTiptap(element: React.ReactElement) {
  return render(
    React.createElement(MarkdownRendererProvider, { renderer: TiptapMarkdown }, element),
  );
}

test("with the Tiptap provider, text content renders through TiptapMarkdown", () => {
  const view = withTiptap(
    React.createElement(TextContent, { node: { type: "text", content: MARKDOWN } }),
  );

  assert.ok(view.container.querySelector("a[data-footnote-ref]"));
});

test("with the Tiptap provider, expanded reasoning renders through TiptapMarkdown", () => {
  const view = withTiptap(
    React.createElement(Reasoning, { node: { type: "reasoning", content: MARKDOWN } }),
  );
  fireEvent.click(screen.getByRole("button", { name: "Expand reasoning" }));

  assert.ok(view.container.querySelector("a[data-footnote-ref]"));
});

test("with the Tiptap provider, every plan part renders through TiptapMarkdown", () => {
  const view = withTiptap(
    React.createElement(PlanCard, {
      leadingMarkdown: MARKDOWN,
      markdown: MARKDOWN,
      trailingMarkdown: MARKDOWN,
      isClosed: true,
    }),
  );

  assert.equal(view.container.querySelectorAll("a[data-footnote-ref]").length, 3);
});
