import assert from "node:assert/strict";
import test from "node:test";
import { setupDomEnvironment } from "@agw/test-harness";

import type { SearchableSelectOption } from "./searchable-select.tsx";

const { React, act, fireEvent, render, screen, waitFor } = await setupDomEnvironment();
const { SearchableSelect } = await import("./searchable-select.tsx");

const options: SearchableSelectOption[] = [
  { value: "sonnet", title: "Claude Sonnet", subtitle: "claude-sonnet-5", group: "Anthropic" },
  {
    value: "haiku",
    title: "Claude Haiku",
    subtitle: "claude-haiku-4-5",
    group: "Anthropic",
    keywords: ["fast", "small"],
  },
  { value: "local", title: "Local model", group: "Self hosted" },
];

function renderSingle(value = "", onValueChange: (next: string) => void = () => {}) {
  return render(
    React.createElement(SearchableSelect, {
      id: "model",
      label: "Model",
      options,
      placeholder: "Select a model",
      searchPlaceholder: "Search models",
      value,
      onValueChange,
    }),
  );
}

async function openPopup(triggerName = "Model") {
  await act(async () => {
    fireEvent.click(screen.getByRole("combobox", { name: triggerName }));
  });
  return screen.findByLabelText("Search models");
}

test("trigger shows the placeholder until an option is selected", async () => {
  const view = renderSingle();
  assert.equal(screen.getByRole("combobox", { name: "Model" }).textContent, "Select a model");
  view.unmount();

  renderSingle("haiku");
  assert.equal(screen.getByRole("combobox", { name: "Model" }).textContent, "Claude Haiku");
});

test("selecting an option reports its value and closes the popup", async () => {
  const selected: string[] = [];
  renderSingle("", (next) => selected.push(next));
  await openPopup();

  await act(async () => {
    fireEvent.click(screen.getByRole("option", { name: /Local model/ }));
  });

  assert.deepEqual(selected, ["local"]);
  await waitFor(() => assert.equal(screen.queryByRole("listbox"), null));
});

test("search matches an option through its keywords", async () => {
  renderSingle();
  const searchBox = await openPopup();

  await act(async () => {
    fireEvent.change(searchBox, { target: { value: "fast" } });
  });

  await waitFor(() => {
    assert.equal(screen.queryByRole("option", { name: /Claude Haiku/ }) !== null, true);
    assert.equal(screen.queryByRole("option", { name: /Claude Sonnet/ }), null);
  });
});

test("search without a match reports an empty result", async () => {
  renderSingle();
  await openPopup();

  await act(async () => {
    fireEvent.change(screen.getByLabelText("Search models"), {
      target: { value: "no such model" },
    });
  });

  await waitFor(() => assert.ok(screen.getByText("No results.")));
});

test("options keep their group headings", async () => {
  renderSingle();
  await openPopup();

  await waitFor(() => {
    assert.ok(screen.getByText("Anthropic"));
    assert.ok(screen.getByText("Self hosted"));
  });
});

test("clearing a selection reports an empty value", async () => {
  const selected: string[] = [];
  renderSingle("haiku", (next) => selected.push(next));
  await openPopup();

  await act(async () => {
    fireEvent.click(screen.getByRole("button", { name: "Clear selection" }));
  });

  assert.deepEqual(selected, [""]);
});

test("an unselected combobox offers no clear action", async () => {
  renderSingle();
  await openPopup();

  assert.equal(screen.queryByRole("button", { name: "Clear selection" }), null);
});

test("multiple selection accumulates values and announces the count", async () => {
  const selected: string[][] = [];
  render(
    React.createElement(SearchableSelect, {
      id: "models",
      label: "Models",
      options,
      placeholder: "Select models",
      searchPlaceholder: "Search models",
      multiple: true,
      value: ["haiku"],
      onValueChange: (next: string[]) => selected.push(next),
    }),
  );

  assert.equal(screen.getByRole("combobox", { name: "Models" }).textContent, "1 selected");
  await openPopup("Models");
  assert.equal(
    screen.getByRole("listbox", { name: "Models" }).getAttribute("aria-multiselectable"),
    "true",
  );

  await act(async () => {
    fireEvent.click(screen.getByRole("option", { name: /Claude Sonnet/ }));
  });

  assert.deepEqual(selected, [["haiku", "sonnet"]]);
});

test("multiple selection shows the caller's selection text", () => {
  render(
    React.createElement(SearchableSelect, {
      id: "models",
      label: "Models",
      options,
      placeholder: "Select models",
      searchPlaceholder: "Search models",
      multiple: true,
      value: ["haiku", "sonnet"],
      selectionText: "Claude Haiku, Claude Sonnet",
      onValueChange: () => {},
    }),
  );

  assert.equal(
    screen.getByRole("combobox", { name: "Models" }).textContent,
    "Claude Haiku, Claude Sonnet",
  );
});

test("loading and error states replace the option list", async () => {
  const view = render(
    React.createElement(SearchableSelect, {
      id: "model",
      label: "Model",
      options,
      placeholder: "Select a model",
      searchPlaceholder: "Search models",
      isLoading: true,
      value: "",
      onValueChange: () => {},
    }),
  );
  await openPopup();
  await waitFor(() => assert.ok(screen.getByText("Loading...")));
  assert.equal(screen.queryByRole("option"), null);
  view.unmount();

  render(
    React.createElement(SearchableSelect, {
      id: "model",
      label: "Model",
      options,
      placeholder: "Select a model",
      searchPlaceholder: "Search models",
      errorMessage: "Model list unavailable",
      value: "",
      onValueChange: () => {},
    }),
  );
  await openPopup();
  await waitFor(() => assert.ok(screen.getByText("Model list unavailable")));
  assert.equal(screen.queryByRole("option"), null);
});

test("a disabled combobox does not open", async () => {
  render(
    React.createElement(SearchableSelect, {
      id: "model",
      label: "Model",
      options,
      placeholder: "Select a model",
      searchPlaceholder: "Search models",
      disabled: true,
      value: "",
      onValueChange: () => {},
    }),
  );

  await act(async () => {
    fireEvent.click(screen.getByRole("combobox", { name: "Model" }));
  });

  assert.equal(screen.queryByLabelText("Search models"), null);
});
