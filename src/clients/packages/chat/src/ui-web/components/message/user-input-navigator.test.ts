import assert from "node:assert/strict";
import test from "node:test";
import { setupDomEnvironment } from "@agw/test-harness";

import type { UserInputMarker } from "./user-input-navigation";

const environment = await setupDomEnvironment();
const { React, act, fireEvent, render, screen } = environment;
const { document } = environment.window;
const { UserInputNavigator } = await import("./user-input-navigator.tsx");

const markers: UserInputMarker[] = [
  { key: "first", itemIndex: 0, rowIndex: 0, start: 0, preview: "First user input" },
  { key: "second", itemIndex: 3, rowIndex: 4, start: 800, preview: "Second user input" },
];

function renderNavigator(
  navigatorMarkers: UserInputMarker[],
  activeKey: string | null,
  selected: string[] = [],
) {
  return render(
    React.createElement(UserInputNavigator, {
      markers: navigatorMarkers,
      activeKey,
      height: 240,
      onSelect: (key: string) => selected.push(key),
    }),
  );
}

function anchor(preview: string) {
  return screen.getByRole("button", { name: `Jump to user input: ${preview}` });
}

test("the navigator marks the active user input and selects its message key", () => {
  const selected: string[] = [];
  renderNavigator(markers, "first", selected);

  assert.ok(screen.getByRole("navigation", { name: "User input navigation" }));
  assert.equal(anchor("First user input").getAttribute("aria-current"), "location");
  assert.equal(anchor("Second user input").getAttribute("aria-current"), null);

  fireEvent.click(anchor("Second user input"));

  assert.deepEqual(selected, ["second"]);
});

test("anchors are laid out by the list, not by absolute offsets", () => {
  renderNavigator(markers, "first");

  assert.equal(anchor("First user input").style.top, "");
  assert.equal(anchor("Second user input").style.top, "");
});

test("a preview appears only for the hovered user input", () => {
  renderNavigator(markers, "first");
  assert.equal(screen.queryByRole("tooltip"), null);

  fireEvent.mouseEnter(anchor("Second user input"));
  const preview = screen.getByRole("tooltip");

  assert.equal(preview.textContent?.trim(), "Second user input");
  assert.equal(
    anchor("Second user input").getAttribute("aria-describedby"),
    "user-input-navigation-preview",
  );
  assert.strictEqual(
    preview.parentElement,
    screen.getByRole("navigation", { name: "User input navigation" }),
  );

  fireEvent.mouseLeave(anchor("Second user input"));

  assert.equal(screen.queryByRole("tooltip"), null);
});

test("a focused user input keeps its preview while scrolling", () => {
  renderNavigator(markers, "first");
  const first = anchor("First user input");

  act(() => first.focus());

  assert.strictEqual(document.activeElement, first);
  assert.equal(screen.getByRole("tooltip").textContent?.trim(), "First user input");
  assert.equal(screen.getByRole("tooltip").getAttribute("id"), "user-input-navigation-preview");

  fireEvent.scroll(
    screen.getByRole("navigation", { name: "User input navigation" }).firstElementChild!,
  );

  assert.equal(screen.getByRole("tooltip").textContent?.trim(), "First user input");
  assert.strictEqual(document.activeElement, first);
});

test("each anchor owns its own hover target", () => {
  renderNavigator(
    [
      { ...markers[0], key: "dense-first", preview: "Dense first" },
      { ...markers[1], key: "dense-second", preview: "Dense second" },
    ],
    null,
  );

  fireEvent.mouseEnter(anchor("Dense first"));
  assert.equal(screen.getByRole("tooltip").textContent?.trim(), "Dense first");

  fireEvent.mouseLeave(anchor("Dense first"));
  assert.equal(screen.queryByRole("tooltip"), null);

  fireEvent.mouseEnter(anchor("Dense second"));
  assert.equal(screen.getByRole("tooltip").textContent?.trim(), "Dense second");
});

test("a long marker list keeps every anchor reachable", () => {
  renderNavigator(
    Array.from({ length: 100 }, (_, index) => ({
      ...markers[0],
      key: `marker-${index}`,
      rowIndex: index,
      preview: `User input ${index}`,
    })),
    null,
  );

  assert.equal(screen.getAllByRole("button").length, 100);
});

test("an unloaded input remains selectable", () => {
  const selected: string[] = [];
  renderNavigator(
    [{ key: "unloaded", itemIndex: null, rowIndex: null, start: null, preview: "Earlier input" }],
    null,
    selected,
  );
  fireEvent.click(anchor("Earlier input"));
  assert.deepEqual(selected, ["unloaded"]);
});
