import assert from "node:assert/strict";
import { readFile } from "node:fs/promises";
import test, { after, afterEach } from "node:test";
import { JSDOM } from "jsdom";
import * as React from "react";

import type { UserInputMarker } from "./user-input-navigation";

const dom = new JSDOM("<!doctype html><html><body></body></html>", {
  pretendToBeVisual: true,
  url: "http://localhost/",
});
const { window } = dom;

for (const [name, value] of Object.entries({
  window,
  document: window.document,
  navigator: window.navigator,
  HTMLElement: window.HTMLElement,
  Element: window.Element,
  Node: window.Node,
  Event: window.Event,
  MouseEvent: window.MouseEvent,
  FocusEvent: window.FocusEvent,
  getComputedStyle: window.getComputedStyle.bind(window),
  requestAnimationFrame: window.requestAnimationFrame.bind(window),
  cancelAnimationFrame: window.cancelAnimationFrame.bind(window),
})) {
  Object.defineProperty(globalThis, name, { configurable: true, value });
}

(globalThis as typeof globalThis & { IS_REACT_ACT_ENVIRONMENT: boolean }).IS_REACT_ACT_ENVIRONMENT =
  true;

const { act, cleanup, fireEvent, render, screen } = await import("@testing-library/react");
const { UserInputNavigator } = await import("./user-input-navigator.tsx");

afterEach(() => cleanup());
after(() => dom.window.close());

test("navigator renders standalone anchors without a vertical rail", async () => {
  const source = await readFile(new URL("./user-input-navigator.tsx", import.meta.url), "utf8");

  assert.doesNotMatch(source, /inset-y-0 left-2 w-px/);
});

const markers: UserInputMarker[] = [
  {
    key: "first",
    itemIndex: 0,
    rowIndex: 0,
    start: 0,
    preview: "First user input",
  },
  {
    key: "second",
    itemIndex: 3,
    rowIndex: 4,
    start: 800,
    preview: "Second user input",
  },
];

test("navigator exposes the active user input and selects its virtual row", () => {
  let selectedRow = -1;
  render(
    React.createElement(UserInputNavigator, {
      markers,
      activeKey: "first",
      height: 240,
      onSelect: (rowIndex: number) => {
        selectedRow = rowIndex;
      },
    }),
  );

  const navigation = screen.getByRole("navigation", { name: "User input navigation" });
  const first = screen.getByRole("button", { name: "Jump to user input: First user input" });
  const second = screen.getByRole("button", { name: "Jump to user input: Second user input" });
  const scrollPane = navigation.firstElementChild;
  const markerList = scrollPane?.firstElementChild;
  assert.ok(scrollPane);
  assert.ok(markerList);
  assert.match(navigation.className, /w-6/);
  assert.doesNotMatch(navigation.className, /absolute/);
  assert.match(scrollPane.className, /overflow-y-auto/);
  assert.match(markerList.className, /min-h-full[^"\n]*flex-col[^"\n]*justify-center/);
  assert.match(first.className, /h-6 w-6/);
  assert.equal(first.getAttribute("aria-current"), "location");
  assert.equal(second.getAttribute("aria-current"), null);
  assert.equal(first.style.top, "");
  assert.equal(second.style.top, "");

  fireEvent.click(second);
  assert.equal(selectedRow, 4);
});

test("navigator shows only the focused or precisely hovered user input preview", () => {
  let selectedRow = -1;
  const view = render(
    React.createElement(UserInputNavigator, {
      markers,
      activeKey: "first",
      height: 240,
      onSelect: (rowIndex: number) => {
        selectedRow = rowIndex;
      },
    }),
  );

  const first = screen.getByRole("button", { name: "Jump to user input: First user input" });
  const second = screen.getByRole("button", { name: "Jump to user input: Second user input" });
  const navigation = screen.getByRole("navigation", { name: "User input navigation" });
  const scrollPane = navigation.firstElementChild;
  assert.ok(scrollPane);
  assert.equal(screen.queryByRole("tooltip"), null);

  fireEvent.mouseEnter(second);
  const hoveredPreview = screen.getByRole("tooltip");
  assert.equal(hoveredPreview.textContent?.trim(), "Second user input");
  assert.strictEqual(hoveredPreview.parentElement, navigation);
  assert.equal(second.getAttribute("aria-describedby"), "user-input-navigation-preview");

  fireEvent.mouseLeave(second);
  assert.equal(screen.queryByRole("tooltip"), null);

  fireEvent.click(second);
  assert.equal(selectedRow, 4);

  act(() => first.focus());
  assert.strictEqual(document.activeElement, first);
  assert.equal(screen.getByRole("tooltip").textContent?.trim(), "First user input");
  assert.equal(screen.getByRole("tooltip").getAttribute("id"), "user-input-navigation-preview");

  first.getBoundingClientRect = () =>
    ({
      top: 100,
      height: 24,
      bottom: 124,
      left: 0,
      right: 24,
      width: 24,
      x: 0,
      y: 100,
      toJSON() {},
    }) as DOMRect;
  fireEvent.scroll(scrollPane);
  assert.equal(screen.getByRole("tooltip").textContent?.trim(), "First user input");
  assert.equal(screen.getByRole("tooltip").style.top, "64px");

  view.unmount();
});

test("navigator hover areas use separate twenty-four-pixel targets", () => {
  const denseMarkers: UserInputMarker[] = [
    { ...markers[0], key: "dense-first", preview: "Dense first" },
    { ...markers[1], key: "dense-second", preview: "Dense second" },
  ];
  render(
    React.createElement(UserInputNavigator, {
      markers: denseMarkers,
      activeKey: null,
      height: 240,
      onSelect: () => {},
    }),
  );

  const first = screen.getByRole("button", { name: "Jump to user input: Dense first" });
  const second = screen.getByRole("button", { name: "Jump to user input: Dense second" });
  assert.match(first.className, /h-6 w-6/);
  assert.match(second.className, /h-6 w-6/);

  fireEvent.mouseEnter(first);
  assert.equal(screen.getByRole("tooltip").textContent?.trim(), "Dense first");

  fireEvent.mouseLeave(first);
  assert.equal(screen.queryByRole("tooltip"), null);

  fireEvent.mouseEnter(second);
  assert.equal(screen.getByRole("tooltip").textContent?.trim(), "Dense second");
});

test("navigator keeps long marker lists inside a scrollable pane", () => {
  const manyMarkers: UserInputMarker[] = Array.from({ length: 100 }, (_, index) => ({
    ...markers[0],
    key: `marker-${index}`,
    rowIndex: index,
    preview: `User input ${index}`,
  }));
  render(
    React.createElement(UserInputNavigator, {
      markers: manyMarkers,
      activeKey: null,
      height: 240,
      onSelect: () => {},
    }),
  );

  const navigation = screen.getByRole("navigation", { name: "User input navigation" });
  const scrollPane = navigation.firstElementChild;
  assert.ok(scrollPane);
  assert.match(scrollPane.className, /overflow-y-auto/);
  assert.doesNotMatch(scrollPane.className, /overscroll-contain/);
  assert.equal(screen.getAllByRole("button").length, 100);
});
