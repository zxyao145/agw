import assert from "node:assert/strict";
import { readFile } from "node:fs/promises";
import test from "node:test";

const PAGE_URL = new URL("../page.tsx", import.meta.url);
const DIALOG_URL = new URL("./visual-agentflow-dialog.tsx", import.meta.url);
const BUILDER_URL = new URL("./visual-agentflow-builder.tsx", import.meta.url);
const TYPES_URL = new URL("../../../../../types/agentflow.ts", import.meta.url);

test("Agentflow summary settings are available only from the Output inspector", async () => {
  const source = await readFile(BUILDER_URL, "utf8");

  assert.match(source, /node\.data\.kind === AgentflowNodeKind\.Output/);
  assert.match(source, /Generate Summary/);
  assert.match(source, /readBoolean\(config\.enableSummary\)/);
  assert.match(source, /setConfig\(\{ enableSummary: enabled \}\)/);
  assert.match(source, /Summary Model Provider/);
  assert.match(source, /checked=\{summaryEnabled\}/);
});

test("Agentflow summary model provider is loaded, edited, saved, and preserved", async () => {
  const [pageSource, dialogSource, builderSource, typesSource] = await Promise.all([
    readFile(PAGE_URL, "utf8"),
    readFile(DIALOG_URL, "utf8"),
    readFile(BUILDER_URL, "utf8"),
    readFile(TYPES_URL, "utf8"),
  ]);

  assert.match(pageSource, /apiGet\("\/api\/model-providers"\)/);
  assert.match(pageSource, /modelProviders=\{modelProvidersQuery\.data \|\| \[\]\}/);
  assert.match(pageSource, /summaryModelProviderId: agentflow\.summaryModelProviderId/);
  assert.match(dialogSource, /modelProviders: ModelProviderDto\[\]/);
  assert.match(dialogSource, /modelProviders=\{modelProviders\}/);
  assert.match(
    builderSource,
    /setSummaryModelProviderId\(editingAgentflow\.summaryModelProviderId \?\? ""\)/,
  );
  assert.match(builderSource, /summaryModelProviderId: summaryModelProviderId \|\| null/);
  assert.match(typesSource, /summaryModelProviderId: string \| null/);
});
