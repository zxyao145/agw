import assert from "node:assert/strict";
import test from "node:test";

import { resolveIcon } from "./extension-contributions";

const ICONS = {
  "icon.svg": "data:image/svg+xml;base64,PHN2Zy8+",
  "icon.png": "data:image/png;base64,AAAA",
  "photo.jpg": "data:image/jpeg;base64,AAAA",
  "picture.webp": "data:image/webp;base64,AAAA",
};

test("resolveIcon draws package SVGs in the text color and shows package images as is", () => {
  assert.deepEqual(resolveIcon("icon.svg", ICONS), { kind: "svg", src: ICONS["icon.svg"] });
  for (const path of ["icon.png", "photo.jpg", "picture.webp"] as const) {
    assert.deepEqual(resolveIcon(path, ICONS), { kind: "image", src: ICONS[path] }, path);
  }
});

test("resolveIcon treats other values as lucide names and defaults to Puzzle", () => {
  assert.deepEqual(resolveIcon("PenTool", ICONS), { kind: "lucide", name: "PenTool" });
  assert.deepEqual(resolveIcon(undefined, ICONS), { kind: "lucide", name: "Puzzle" });
});
