import assert from "node:assert/strict";
import test from "node:test";
import { setupDomEnvironment } from "@agw/test-harness";

const { React, act, fireEvent, render, screen, waitFor, window } = await setupDomEnvironment();
const { default: MdCard } = await import("./md-card.tsx");

const FENCE = String.fromCharCode(96).repeat(3);

function renderMarkdown(mdText: string, enableMath?: boolean) {
  return render(React.createElement(MdCard, { mdText, enableMath }));
}

test("a fenced code block shows its language and block actions", () => {
  const view = renderMarkdown(
    [FENCE + "typescript", "const answer: number = 42;", FENCE].join("\n"),
  );

  assert.ok(view.container.querySelector(".msg-content-md-code-block"));
  assert.ok(screen.getByText("typescript"));
  assert.ok(screen.getByRole("button", { name: "Disable word wrap" }));
  assert.ok(screen.getByRole("button", { name: "Copy code" }));
});

test("a fenced code block without a language falls back to plain", () => {
  renderMarkdown([FENCE, "agentName = claude-code", FENCE].join("\n"));

  assert.ok(screen.getByText("plain"));
});

test("code blocks start wrapped and toggle wrapping on request", () => {
  const view = renderMarkdown([FENCE + "sh", "echo hello", FENCE].join("\n"));
  const body = view.container.querySelector("pre.msg-content-md-code-block-body");

  assert.equal(body?.getAttribute("data-wrap"), "true");
  assert.equal(
    screen.getByRole("button", { name: "Disable word wrap" }).getAttribute("aria-pressed"),
    "true",
  );

  fireEvent.click(screen.getByRole("button", { name: "Disable word wrap" }));

  assert.equal(body?.getAttribute("data-wrap"), "false");
  assert.equal(
    screen.getByRole("button", { name: "Enable word wrap" }).getAttribute("aria-pressed"),
    "false",
  );
});

test("copying a code block puts only its code on the clipboard", async () => {
  renderMarkdown([FENCE + "sh", "dotnet build Agw.slnx", FENCE].join("\n"));

  await act(async () => {
    fireEvent.click(screen.getByRole("button", { name: "Copy code" }));
  });

  assert.equal(await window.navigator.clipboard.readText(), "dotnet build Agw.slnx");
  await waitFor(() => assert.ok(screen.getByRole("button", { name: "Code copied" })));
});

test("an indented code block renders plainly without the block header", () => {
  const view = renderMarkdown(["some text", "", "    const answer = 42;", ""].join("\n"));

  assert.ok(view.container.querySelector("pre.msg-content-md-code"));
  assert.equal(view.container.querySelector(".msg-content-md-code-block"), null);
  assert.equal(screen.queryByRole("button", { name: "Copy code" }), null);
});

test("unfenced diff text does not produce per-line code block headers", () => {
  const view = renderMarkdown(
    [
      "+function getTextContent(node: React.ReactNode): string {",
      "- return React.Children.toArray(node)",
      "",
      "      return String(child);",
    ].join("\n"),
  );

  assert.ok(view.container.querySelector("pre.msg-content-md-code"));
  assert.equal(view.container.querySelector(".msg-content-md-code-block"), null);
});

test("inline code keeps the inline renderer without block controls", () => {
  const view = renderMarkdown("Use `agentName` in this sentence.");
  const code = view.container.querySelector("code.msg-content-md-code");

  assert.equal(code?.textContent, "agentName");
  assert.equal(view.container.querySelector(".msg-content-md-code-block"), null);
  assert.equal(screen.queryByRole("button", { name: "Copy code" }), null);
});

test("markdown lists render as list elements", () => {
  const view = renderMarkdown(["1. first", "2. second", "", "- alpha", "- beta"].join("\n"));

  assert.equal(view.container.querySelectorAll("ol > li").length, 2);
  assert.equal(view.container.querySelectorAll("ul > li").length, 2);
});

test("math renders through KaTeX by default", () => {
  const view = renderMarkdown(String.raw`公式：\(x + 1\)`);

  assert.ok(view.container.querySelector(".katex"));
});

test("math can be turned off so the text stays literal", () => {
  const view = renderMarkdown("$PATH and \\alpha plus `\\beta`", false);

  assert.equal(view.container.querySelector(".katex"), null);
  assert.match(view.container.textContent ?? "", /\$PATH/);
  assert.match(view.container.textContent ?? "", /\\alpha/);
  assert.match(view.container.textContent ?? "", /\\beta/);
});

test("web links open outside the current application", () => {
  renderMarkdown("[Agw](https://github.com/zxyao145/agw)");
  const link = screen.getByRole("link", { name: /Agw/ });

  assert.equal(link.getAttribute("href"), "https://github.com/zxyao145/agw");
  assert.equal(link.getAttribute("target"), "_blank");
  assert.equal(link.getAttribute("rel"), "noreferrer");
});

test("non-web links render as file references that do not navigate", () => {
  const view = renderMarkdown("[Chat.tsx](/Users/example/Chat.tsx:434)");

  assert.equal(screen.queryByRole("link"), null);
  assert.equal(view.container.querySelector("[href]"), null);
  assert.match(view.container.textContent ?? "", /Chat\.tsx/);
  assert.equal(
    view.container.querySelector("span")?.getAttribute("title"),
    "/Users/example/Chat.tsx:434",
  );
});

test("tables render inside a scrollable labelled region", () => {
  const view = renderMarkdown(
    ["| Provider | Mode |", "| --- | --- |", "| SQLite | InProcess |"].join("\n"),
  );

  const region = screen.getByRole("region", { name: "Scrollable table" });
  assert.ok(region.querySelector("table.msg-content-md-table"));
  assert.ok(view.container.querySelector("th"));
  assert.ok(screen.getByRole("cell", { name: "SQLite" }));
});
