import assert from "node:assert/strict";
import { after, test } from "node:test";

import { createEditHost } from "../editor/test-edit-host";
import { isMermaidLanguage, renderMermaid } from "./mermaid-render";
import { DARK_NODE, darkTheme, flowchart, LIGHT_NODE, svgId } from "./test-mermaid";

const host = createEditHost();
after(() => host.close());

const LIGHT = host.api.theme.get();

test("isMermaidLanguage matches the word mermaid in any case", () => {
  assert.equal(isMermaidLanguage("mermaid"), true);
  assert.equal(isMermaidLanguage("Mermaid"), true);
  assert.equal(isMermaidLanguage(null), false);
  assert.equal(isMermaidLanguage("ts"), false);
  assert.equal(isMermaidLanguage("mermaid-js"), false);
});

test("renderMermaid draws the source with a new SVG id for every render", async () => {
  const source = flowchart("资料收集 Agent", "分析 Agent");
  const first = await renderMermaid(source, LIGHT);
  const second = await renderMermaid(source, LIGHT);
  assert.ok(first.includes("资料收集 Agent") && first.includes("分析 Agent"));
  assert.equal(svgId(second), svgId(first) + 1);
});

test("renderMermaid uses mermaid's default theme for light hosts and its dark theme for dark hosts", async () => {
  const source = flowchart("A", "B");
  const dark = await renderMermaid(source, darkTheme(LIGHT));
  const light = await renderMermaid(source, LIGHT);
  assert.match(dark, DARK_NODE);
  assert.doesNotMatch(dark, LIGHT_NODE);
  assert.match(light, LIGHT_NODE);
  assert.doesNotMatch(light, DARK_NODE);
});
