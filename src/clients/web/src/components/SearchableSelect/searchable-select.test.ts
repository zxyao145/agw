import assert from "node:assert/strict";
import { readFile } from "node:fs/promises";
import test from "node:test";

const COMPONENT_URL = new URL("./searchable-select.tsx", import.meta.url);

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
