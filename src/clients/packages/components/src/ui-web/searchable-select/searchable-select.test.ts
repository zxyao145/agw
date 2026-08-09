import assert from "node:assert/strict";
import { readFile } from "node:fs/promises";
import test from "node:test";

const COMPONENT_URL = new URL("./searchable-select.tsx", import.meta.url);
const PACKAGES_URL = new URL("../../../../", import.meta.url);
const AGENT_SELECTOR_URL = new URL("chat/src/ui-web/components/agent-selector.tsx", PACKAGES_URL);
const COMBOBOX_URL = new URL("components/src/ui-web/shadcn/combobox.tsx", PACKAGES_URL);

test("SearchableSelect exposes type-safe single and multiple selection props", async () => {
  const source = await readFile(COMPONENT_URL, "utf8");

  assert.match(source, /type SearchableSelectSingleProps = \{[\s\S]*multiple\?: false/);
  assert.match(source, /value: string;[\s\S]*onValueChange: \(value: string\) => void/);
  assert.match(source, /type SearchableSelectMultipleProps = \{[\s\S]*multiple: true/);
  assert.match(source, /value: string\[\];[\s\S]*onValueChange: \(value: string\[\]\) => void/);
  assert.match(
    source,
    /type SearchableSelectProps = SearchableSelectBaseProps &[\s\S]*SearchableSelectSingleProps \| SearchableSelectMultipleProps/,
  );
});

test("SearchableSelect preserves controlled multiple selection behavior", async () => {
  const source = await readFile(COMPONENT_URL, "utf8");

  assert.match(source, /<Combobox<string, true>/);
  assert.match(source, /multiple[\s\S]*value=\{props\.value\}/);
  assert.match(source, /onValueChange=\{props\.onValueChange\}/);
  assert.match(source, /aria-multiselectable=\{props\.multiple \|\| undefined\}/);
  assert.match(source, /<ComboboxItem[\s\S]*value=\{option\.value\}/);
  assert.match(source, /details\.reason !== "item-press"/);
});

test("SearchableSelect composes the current Shadcn Combobox", async () => {
  const source = await readFile(COMPONENT_URL, "utf8");
  const comboboxSource = await readFile(COMBOBOX_URL, "utf8");

  assert.match(source, /from "\.\.\/shadcn\/combobox";/);
  assert.match(source, /<ComboboxTrigger/);
  assert.match(source, /<ComboboxContent/);
  assert.match(source, /<ComboboxInput/);
  assert.match(source, /<ComboboxList/);
  assert.match(source, /<ComboboxItem/);
  assert.doesNotMatch(source, /from "\.\.\/shadcn\/popover"/);
  assert.match(comboboxSource, /Combobox as ComboboxPrimitive.*from "@base-ui\/react"/);
});

test("SearchableSelect keeps its Combobox focus scope inside modal surfaces", async () => {
  const source = await readFile(COMPONENT_URL, "utf8");
  const comboboxSource = await readFile(COMBOBOX_URL, "utf8");

  assert.match(source, /data-slot="dialog-content"/);
  assert.match(source, /data-slot="sheet-content"/);
  assert.match(source, /data-slot="drawer-content"/);
  assert.match(source, /closest<HTMLElement>\(MODAL_CONTENT_SELECTOR\)/);
  assert.match(source, /portalContainer=\{portalContainer\}/);
  assert.match(source, /initialFocus=\{searchInputRef\}/);
  assert.match(comboboxSource, /<ComboboxPrimitive\.Portal container=\{portalContainer\}>/);
});

test("AgentSelector forwards the optional Select size to SearchableSelect", async () => {
  const source = await readFile(COMPONENT_URL, "utf8");
  const agentSelectorSource = await readFile(AGENT_SELECTOR_URL, "utf8");

  assert.match(source, /size\?: "default" \| "sm"/);
  assert.match(source, /size = "default"/);
  assert.match(source, /<Button[\s\S]*size=\{size\}/);
  assert.match(agentSelectorSource, /size\?: "default" \| "sm"/);
  assert.match(agentSelectorSource, /size = "default"/);
  assert.match(agentSelectorSource, /<SearchableSelect[\s\S]*size=\{size\}/);
});
