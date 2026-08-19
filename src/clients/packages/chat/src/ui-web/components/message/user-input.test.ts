import assert from "node:assert/strict";
import { readFileSync } from "node:fs";
import test from "node:test";

const source = readFileSync(new URL("./user-input.tsx", import.meta.url), "utf8");

test("suggestion kind badge keeps its content height inside the flex row", () => {
  assert.match(source, /<Badge className="[^"]*\bh-fit\b[^"]*\bself-start\b[^"]*">/);
});

test("suggestions render above the composer without covering the textarea", () => {
  assert.match(source, /className="[^"]*\bbottom-full\b[^"]*"/);
  assert.doesNotMatch(source, /\bbottom-18\b/);
  assert.match(
    source,
    /\{\/\* Input area with textarea and action button \*\/\}\s*<div className="relative">\s*\{suggestions\}/,
  );
});

test("composer defaults to one text line with a floating circular action", () => {
  assert.match(source, /rows = 1/);
  assert.match(source, /maxHeight = "max-h-60"/);
  assert.match(source, /\bagw-scrollbar min-h-\[1lh\]/);
  assert.doesNotMatch(source, /\bmin-h-28\b/);
  assert.match(source, /className="[^"]*\brounded-xl\b[^"]*"/);
  assert.match(source, /className="[^"]*\bbg-background\b[^"]*\bshadow-sm\b[^"]*"/);
  assert.doesNotMatch(source, /bg-backgroundshadow-sm/);
  assert.match(source, /\bagw-scrollbar\b/);
  assert.match(source, /className="absolute left-2 right-2 bottom-2 h-7 flex justify-between"/);
  assert.match(source, /UserInput\.BottomLeft/);
  assert.match(source, /size="icon-sm"[\s\S]*?className="rounded-full size-7"/);
  assert.match(source, /<ArrowUp className="size-5" \/>/);
});

test("insertText preserves the draft and inserts at the current selection", () => {
  assert.match(source, /textarea\?\.selectionStart \?\? input\.length/);
  assert.match(
    source,
    /input\.slice\(0, selectionStart\) \+ insertedText \+ input\.slice\(selectionEnd\)/,
  );
});

test("suggestions use the current caret and restore it after selection", () => {
  assert.match(
    source,
    /onChange=\{\(e\) => onInputChange\(e\.target\.value, e\.target\.selectionStart\)\}/,
  );
  assert.match(source, /suggestionCaretRef\.current = caretIndex/);
  assert.match(source, /onSuggestion\(value, caretIndex\)/);
  assert.match(source, /replaceSuggestion\(input, suggestion\.text, suggestionCaretRef\.current\)/);
  assert.match(source, /setSelectionRange\(replacement\.caretIndex, replacement\.caretIndex\)/);
});

test("suggestion descriptions use phrasing elements inside ItemDescription", () => {
  assert.match(source, /<ItemDescription>[\s\S]*?<span className="flex item-start">/);
  assert.match(source, /<span className="text-\[11px\]">\{suggestion\.description\}<\/span>/);
  assert.doesNotMatch(source, /<ItemDescription>[\s\S]*?<div className="flex item-start">/);
  assert.doesNotMatch(source, /<ItemDescription>[\s\S]*?<p className="text-\[11px\]">/);
});
