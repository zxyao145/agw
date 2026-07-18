import assert from "node:assert/strict";
import { readFile } from "node:fs/promises";
import test from "node:test";

const COMPONENT_URL = new URL("./searchable-select.tsx", import.meta.url);
const PACKAGES_URL = new URL("../../../../", import.meta.url);
const AGENT_SELECTOR_URL = new URL("chat/src/ui-web/components/agent-selector.tsx", PACKAGES_URL);
const POPOVER_URL = new URL("components/src/ui-web/shadcn/popover.tsx", PACKAGES_URL);

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

test("SearchableSelect keeps multiple selection open and exposes selected option state", async () => {
  const source = await readFile(COMPONENT_URL, "utf8");

  assert.match(source, /aria-multiselectable=\{props\.multiple \|\| undefined\}/);
  assert.match(source, /role="option"/);
  assert.match(source, /aria-selected=\{isSelected\}/);
  assert.match(source, /props\.value\.filter\(\(value\) => value !== optionValue\)/);
  assert.match(source, /\[\.\.\.props\.value, optionValue\]/);
  assert.match(source, /if \(!props\.multiple\) \{[\s\S]*setOpen\(false\)/);
});

test("SearchableSelect portals its menu outside scrollable form containers", async () => {
  const source = await readFile(COMPONENT_URL, "utf8");
  const popoverSource = await readFile(POPOVER_URL, "utf8");

  assert.match(
    source,
    /import \{ Popover, PopoverContent, PopoverTrigger \} from "\.\.\/shadcn\/popover";/,
  );
  assert.match(source, /<Popover (?:modal )?open=\{open\} onOpenChange=\{handleOpenChange\}>/);
  assert.match(source, /<PopoverTrigger asChild>/);
  assert.match(source, /<PopoverContent[\s\S]*w-\(--radix-popover-trigger-width\)/);
  assert.doesNotMatch(source, /absolute left-0 top-full/);
  assert.match(popoverSource, /<PopoverPrimitive\.Portal container=\{portalContainer\}>/);
});

test("SearchableSelect keeps its portalled menu scrollable inside modal dialogs", async () => {
  const source = await readFile(COMPONENT_URL, "utf8");

  assert.match(source, /<Popover modal open=\{open\} onOpenChange=\{handleOpenChange\}>/);
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
