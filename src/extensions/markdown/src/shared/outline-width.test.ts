import assert from "node:assert/strict";
import { beforeEach, test } from "node:test";

import { JSDOM } from "jsdom";

// localStorage 需要有来源的页面地址。localStorage needs a page URL with an origin.
const { window } = new JSDOM("<!doctype html><html><body></body></html>", {
  url: "http://localhost/",
});
Object.assign(globalThis, {
  window,
  document: window.document,
  localStorage: window.localStorage,
  getComputedStyle: window.getComputedStyle.bind(window),
});

const {
  OUTLINE_WIDTH_KEY,
  applyOutlineWidth,
  outlineWidthRange,
  readOutlineSizes,
  readStoredOutlineWidth,
  storeOutlineWidth,
} = await import("./outline-width");

const html = window.document.documentElement;

function shown(): { attribute: boolean; width: string } {
  return {
    attribute: html.hasAttribute("data-outline-width"),
    width: html.style.getPropertyValue("--outline-user-width"),
  };
}

beforeEach(() => {
  window.localStorage.clear();
  html.removeAttribute("data-outline-width");
  html.style.removeProperty("--outline-user-width");
});

test("outlineWidthRange keeps the minimum document width and never goes below the minimum", () => {
  const sizes = { min: 240, gutter: 16, documentMin: 480 };
  assert.deepEqual(outlineWidthRange(1200, sizes), { min: 240, max: 704 });
  assert.deepEqual(outlineWidthRange(700, sizes), { min: 240, max: 240 });
});

test("readOutlineSizes reads px lengths and throws on anything else", () => {
  const element = window.document.createElement("div");
  window.document.body.append(element);
  element.style.setProperty("--outline-min-width", "240px");
  element.style.setProperty("--outline-gutter", "16px");
  element.style.setProperty("--document-min-width", "480px");
  assert.deepEqual(readOutlineSizes(element), { min: 240, gutter: 16, documentMin: 480 });
  element.style.setProperty("--document-min-width", "30rem");
  assert.throws(() => readOutlineSizes(element), /--document-min-width is not a px length/);
  element.remove();
});

test("storeOutlineWidth stores and shows the width, and null restores the automatic width", () => {
  storeOutlineWidth(320);
  assert.equal(window.localStorage.getItem(OUTLINE_WIDTH_KEY), "320");
  assert.equal(readStoredOutlineWidth(), 320);
  assert.deepEqual(shown(), { attribute: true, width: "320px" });
  storeOutlineWidth(null);
  assert.equal(window.localStorage.getItem(OUTLINE_WIDTH_KEY), null);
  assert.equal(readStoredOutlineWidth(), null);
  assert.deepEqual(shown(), { attribute: false, width: "" });
});

test("readStoredOutlineWidth throws on a stored value that is not a positive integer", () => {
  for (const value of ["abc", "0", "320.5", "-5", " 320"]) {
    window.localStorage.setItem(OUTLINE_WIDTH_KEY, value);
    assert.throws(() => readStoredOutlineWidth(), /The stored outline width is invalid/, value);
  }
});

test("applyOutlineWidth shows the stored width and follows storage events from other pages", () => {
  window.localStorage.setItem(OUTLINE_WIDTH_KEY, "300");
  applyOutlineWidth();
  assert.deepEqual(shown(), { attribute: true, width: "300px" });

  // 另一个页面写入存储后，这个页面收到 storage 事件。Another page writes the storage, and this page gets a storage event.
  window.localStorage.setItem(OUTLINE_WIDTH_KEY, "360");
  window.dispatchEvent(new window.StorageEvent("storage", { key: "other.key" }));
  assert.deepEqual(shown(), { attribute: true, width: "300px" });
  window.dispatchEvent(new window.StorageEvent("storage", { key: OUTLINE_WIDTH_KEY }));
  assert.deepEqual(shown(), { attribute: true, width: "360px" });

  window.localStorage.clear();
  window.dispatchEvent(new window.StorageEvent("storage", { key: null }));
  assert.deepEqual(shown(), { attribute: false, width: "" });
});
