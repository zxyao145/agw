import assert from "node:assert/strict";
import test from "node:test";
import { setupDomEnvironment } from "@agw/test-harness";
import { registerMarkdownRendererCases } from "../components/message/renders/markdown-renderer-cases.test-helper";

const environment = await setupDomEnvironment();
const { React, act, fireEvent, render, screen, window } = environment;
const { getSchema } = await import("@tiptap/core");
const { default: MdCard } = await import("../components/message/renders/md-card.tsx");
const { markdownExtensions } = await import("./markdown-extensions.ts");
const { markMapping, nodeMapping } = await import("./markdown-node-mapping.tsx");
const { TiptapMarkdown } = await import("./tiptap-markdown.tsx");

const FENCE = "```";

function renderMarkdown(markdown: string) {
  return render(React.createElement(TiptapMarkdown, { markdown }));
}

registerMarkdownRendererCases(environment, renderMarkdown);

const FOOTNOTES = "A[^n] and again[^n].\n\n[^n]: Footnote text.";

test("footnotes render references, a labelled section and back references", () => {
  const { container } = renderMarkdown(FOOTNOTES);
  const references = [...container.querySelectorAll("sup > a[data-footnote-ref]")];
  const section = container.querySelector("section[data-footnotes]");
  const label = section?.querySelector("h2.sr-only");
  const item = section?.querySelector("ol > li");

  assert.deepEqual(
    references.map((reference) => reference.textContent),
    ["1", "1"],
  );
  assert.ok(label?.id);
  assert.equal(label.textContent, "Footnotes");
  for (const reference of references) {
    assert.equal(reference.getAttribute("aria-describedby"), label.id);
    assert.equal(reference.getAttribute("href"), `#${item?.id}`);
  }
  assert.equal(item?.getAttribute("tabindex"), "-1");

  const backReferences = [...(item?.querySelectorAll("p > a[data-footnote-backref]") ?? [])];
  assert.deepEqual(
    backReferences.map((link) => [link.innerHTML, link.getAttribute("href")]),
    [
      ["↩", `#${references[0].id}`],
      ["↩<sup>2</sup>", `#${references[1].id}`],
    ],
  );
  assert.deepEqual(
    backReferences.map((link) => link.getAttribute("aria-label")),
    ["Back to reference 1", "Back to reference 1-2"],
  );
});

test("two messages never share footnote ids", () => {
  const { container } = render(
    React.createElement(
      React.Fragment,
      null,
      React.createElement(TiptapMarkdown, { markdown: FOOTNOTES }),
      React.createElement(TiptapMarkdown, { markdown: FOOTNOTES }),
    ),
  );
  const ids = [...container.querySelectorAll("[id]")].map((element) => element.id);

  assert.equal(ids.length, 8);
  assert.equal(new Set(ids).size, ids.length);
});

test("clicking a footnote reference focuses the footnote without changing the hash", () => {
  const { container } = renderMarkdown(FOOTNOTES);
  const hash = window.location.hash;

  fireEvent.click(container.querySelector("a[data-footnote-ref]")!);

  assert.equal(window.document.activeElement, container.querySelector("section li"));
  assert.equal(window.location.hash, hash);
});

test("tables split head and body rows, align cells and keep cell text bare", () => {
  const { container } = renderMarkdown("| a | b |\n| :- | -: |\n| 1 | 2 |");

  assert.equal(container.querySelectorAll("thead th").length, 2);
  assert.equal(container.querySelectorAll("tbody td").length, 2);
  assert.equal(container.querySelector("td p, th p"), null);
  assert.deepEqual(
    [...container.querySelectorAll<HTMLElement>("th, td")].map((cell) => cell.style.textAlign),
    ["left", "right", "left", "right"],
  );
});

test("list item content always sits in a paragraph", () => {
  for (const markdown of ["- a\n- b", "- a\n\n- b"]) {
    const { container, unmount } = renderMarkdown(markdown);
    assert.equal(container.querySelectorAll("li > p.msg-content-md-p").length, 2, markdown);
    unmount();
  }
});

test("task items render disabled checkboxes inside their first paragraph", () => {
  const { container } = renderMarkdown("- [ ] a\n\n- [x] b");
  const lists = container.querySelectorAll("ul");
  const checkboxes = [...container.querySelectorAll<HTMLInputElement>("li > p > input")];

  assert.equal(lists.length, 1);
  assert.equal(lists[0].querySelectorAll(":scope > li > p").length, 2);
  assert.deepEqual(
    checkboxes.map((checkbox) => [checkbox.type, checkbox.disabled, checkbox.checked]),
    [
      ["checkbox", true, false],
      ["checkbox", true, true],
    ],
  );
  assert.equal(checkboxes[0].parentElement?.textContent, " a");
});

test("ordered lists keep their start number and strikethrough renders as del", () => {
  const { container } = renderMarkdown("3. three\n4. ~~four~~");

  assert.equal(container.querySelector("ol")?.getAttribute("start"), "3");
  assert.equal(container.querySelector("del")?.textContent, "four");
});

test("python code keeps its indentation on screen and when copied", async () => {
  const code = "if ready:\n    run()";
  const { container } = renderMarkdown([FENCE + "py", code, FENCE].join("\n"));

  assert.equal(container.querySelector("pre code")?.textContent, code + "\n");
  await act(async () => {
    fireEvent.click(screen.getByRole("button", { name: "Copy code" }));
  });
  assert.equal(await window.navigator.clipboard.readText(), code);
});

test("raw HTML produces no elements", () => {
  const { container } = renderMarkdown(
    '<script>alert(1)</script>\n\nhello <b>bold</b> <img src="x" onerror="alert(1)">',
  );

  assert.equal(container.querySelector("script, b, img"), null);
  assert.match(container.textContent ?? "", /<b>bold<\/b>/);
});

test("images default their alt text, skip empty sources and stay inline", () => {
  const { container } = renderMarkdown("see ![](a.png) and ![x]() here");
  const images = container.querySelectorAll("p > img");

  assert.equal(images.length, 1);
  assert.equal(images[0].getAttribute("alt"), "Markdown image");
});

test("math renders inline and display formulas", () => {
  const { container } = renderMarkdown("Inline $x^2$.\n\n$$\ny = 1\n$$");

  assert.ok(container.querySelector("p .katex"));
  assert.ok(container.querySelector(".katex-display"));
});

test("appending content keeps a code block's wrap state and existing elements", () => {
  const first = [FENCE + "sh", "echo hello", FENCE, "", "![x](x.png)"].join("\n");
  const { container, rerender } = renderMarkdown(first);
  fireEvent.click(screen.getByRole("button", { name: "Disable word wrap" }));
  const pre = container.querySelector("pre");
  const image = container.querySelector("img");

  rerender(React.createElement(TiptapMarkdown, { markdown: first + " more\n\nNext paragraph" }));

  assert.equal(
    screen.getByRole("button", { name: "Enable word wrap" }).getAttribute("aria-pressed"),
    "false",
  );
  assert.equal(container.querySelector("pre"), pre);
  assert.equal(container.querySelector("img"), image);
});

test("a list that turns loose keeps its existing image", () => {
  for (const prefix of ["- before ![x](x.png)\n\n", "- ![x](x.png)\n\n"]) {
    const { container, rerender, unmount } = renderMarkdown(prefix);
    const image = container.querySelector("li > p > img");
    assert.ok(image, prefix);

    rerender(React.createElement(TiptapMarkdown, { markdown: prefix + "- y" }));

    assert.equal(container.querySelector("li > p > img"), image, prefix);
    unmount();
  }
});

test("text appended after a top-level image keeps the image", () => {
  const { container, rerender } = renderMarkdown("![x](x.png)");
  const image = container.querySelector("p > img");
  assert.ok(image);

  rerender(React.createElement(TiptapMarkdown, { markdown: "![x](x.png) and more" }));

  assert.equal(container.querySelector("p > img"), image);
});

test("every schema node and mark has a mapping", () => {
  const schema = getSchema(markdownExtensions);

  assert.deepEqual(
    Object.keys(schema.nodes).filter((name) => !(name in nodeMapping)),
    [],
  );
  assert.deepEqual(
    Object.keys(schema.marks).filter((name) => !(name in markMapping)),
    [],
  );
});

const COMPARISON_INPUTS = [
  "# Heading\n\nText with **bold**, *em*, ~~del~~ and `code`.",
  "- a\n- b\n\n1. one\n2. two\n\n- [ ] todo\n- [x] done",
  "- loose\n\n- items\n  - nested",
  "| a | b |\n| :- | -: |\n| 1 | `code` |",
  [FENCE + "ts", "const a = 1;", FENCE, "", "    indented", "", "> quote"].join("\n"),
  "[web](https://example.com) and [file](/tmp/a.ts) ![img](a.png)",
  "Note[^1] and more[^1].\n\n[^1]: The note.",
  "Math $x^2$ and\n\n$$\ny = 1\n$$\n\n---\n\nEnd",
];

function comparable(container: HTMLElement) {
  const clone = container.cloneNode(true) as HTMLElement;
  for (const formula of clone.querySelectorAll(".katex")) formula.remove();
  // react-markdown 在块级元素之间输出换行文字，对照时去掉全部空白。
  // react-markdown emits newline text between block elements, so the comparison drops all whitespace.
  const text = (clone.textContent ?? "").replace(/\s+/g, "");
  const counts = Object.fromEntries(
    ["table", "th", "td", "li", "pre", ".msg-content-md-code-block", "img", "input", ".katex"].map(
      (selector) => [selector, container.querySelectorAll(selector).length],
    ),
  );
  return { text, counts };
}

test("the Tiptap output matches MdCard in text and element counts", () => {
  for (const markdown of COMPARISON_INPUTS) {
    const expected = render(React.createElement(MdCard, { markdown }));
    const actual = renderMarkdown(markdown);

    assert.deepEqual(comparable(actual.container), comparable(expected.container), markdown);
    expected.unmount();
    actual.unmount();
  }
});
