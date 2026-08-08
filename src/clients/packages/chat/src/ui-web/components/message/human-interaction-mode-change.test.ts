import assert from "node:assert/strict";
import { readFileSync } from "node:fs";
import test from "node:test";

const componentSource = readFileSync(
  new URL("./human-interaction-mode-change.tsx", import.meta.url),
  "utf8",
);
const panelSource = readFileSync(new URL("./human-interaction-panel.tsx", import.meta.url), "utf8");

test("mode change interaction requires an explicit human confirmation", () => {
  assert.match(componentSource, /aria-label=\{`Switch to \$\{modeLabel\} mode`\}/);
  assert.match(componentSource, /onClick=\{\(\) => onSubmit\(\{ confirmed: true \}\)\}/);
  assert.match(componentSource, /onClick=\{onCancel\}/);
});

test("human interaction panel renders the mode change confirmation", () => {
  assert.match(panelSource, /if \(request\.modeChange\)/);
  assert.match(panelSource, /<HumanInteractionModeChange/);
});
