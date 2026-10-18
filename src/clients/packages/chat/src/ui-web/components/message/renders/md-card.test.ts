import assert from "node:assert/strict";
import test from "node:test";
import { setupDomEnvironment } from "@agw/test-harness";
import { registerMarkdownRendererCases } from "./markdown-renderer-cases.test-helper";

const environment = await setupDomEnvironment();
const { React, render } = environment;
const { default: MdCard } = await import("./md-card.tsx");

function renderMarkdown(markdown: string, enableMath?: boolean) {
  return render(React.createElement(MdCard, { markdown, enableMath }));
}

registerMarkdownRendererCases(environment, (markdown) => renderMarkdown(markdown));

test("math can be turned off so the text stays literal", () => {
  const view = renderMarkdown("$PATH and \\alpha plus `\\beta`", false);

  assert.equal(view.container.querySelector(".katex"), null);
  assert.match(view.container.textContent ?? "", /\$PATH/);
  assert.match(view.container.textContent ?? "", /\\alpha/);
  assert.match(view.container.textContent ?? "", /\\beta/);
});
