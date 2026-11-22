import assert from "node:assert/strict";
import test from "node:test";
import { setupDomEnvironment } from "@agw/test-harness";

const { React, render, screen, waitFor } = await setupDomEnvironment();
const { TooltipProvider } = await import("@agw/components");
const { ProviderModelsEditor } = await import("./provider-models-editor.tsx");
const { getProviderModelOptions, getVisibleSelectedModelNames } =
  await import("./provider-models.ts");

const existingModels = [
  { id: "existing-model-id", name: "existing-model" },
  { id: "shared-model-id", name: "shared-model" },
];

test("invalidating discovery hides and clears only selections outside the model library", async () => {
  const view = render(
    React.createElement(SelectionHarness, {
      discovery: { modelNames: ["model-a"] },
      isLoading: false,
    }),
  );

  assert.equal(screen.getByText("3 of 3 selected") !== null, true);
  assert.equal(
    screen.getByRole("checkbox", { name: /model-a/ }).getAttribute("aria-checked"),
    "true",
  );

  view.rerender(
    React.createElement(SelectionHarness, {
      discovery: undefined,
      isLoading: false,
    }),
  );

  await waitFor(() => assert.equal(screen.getByText("2 of 2 selected") !== null, true));
  assert.equal(screen.queryByRole("checkbox", { name: /model-a/ }), null);
  assert.equal(
    screen.getByRole("checkbox", { name: /existing-model/ }).getAttribute("aria-checked"),
    "true",
  );
  assert.equal(
    screen.getByRole("checkbox", { name: /shared-model/ }).getAttribute("aria-checked"),
    "true",
  );

  view.rerender(
    React.createElement(SelectionHarness, {
      discovery: { modelNames: ["model-a"] },
      isLoading: false,
    }),
  );

  assert.equal(
    screen.getByRole("checkbox", { name: /model-a/ }).getAttribute("aria-checked"),
    "false",
  );
  assert.equal(screen.getByText("2 of 3 selected") !== null, true);
});

test("loading a reopened model library preserves selected models until its list arrives", async () => {
  const view = render(
    React.createElement(SelectionHarness, {
      discovery: undefined,
      isLoading: true,
    }),
  );

  assert.equal(
    screen.getByLabelText("selected models").textContent,
    "existing-model,shared-model,model-a",
  );
  assert.equal(screen.getByText("Loading models...") !== null, true);

  view.rerender(
    React.createElement(SelectionHarness, {
      discovery: undefined,
      isLoading: false,
      error: new Error("Model list unavailable"),
    }),
  );
  assert.equal(
    screen.getByLabelText("selected models").textContent,
    "existing-model,shared-model,model-a",
  );

  view.rerender(
    React.createElement(SelectionHarness, {
      discovery: undefined,
      isLoading: false,
    }),
  );

  await waitFor(() =>
    assert.equal(
      screen.getByLabelText("selected models").textContent,
      "existing-model,shared-model",
    ),
  );
  assert.equal(
    screen.getByRole("checkbox", { name: /existing-model/ }).getAttribute("aria-checked"),
    "true",
  );
  assert.equal(
    screen.getByRole("checkbox", { name: /shared-model/ }).getAttribute("aria-checked"),
    "true",
  );
  assert.equal(screen.queryByRole("checkbox", { name: /model-a/ }), null);
});

test("create and update payload selections contain only current options", async () => {
  const modelOptions = getProviderModelOptions(existingModels, undefined, "OpenAIResponses");
  const visibleSelections = getVisibleSelectedModelNames(
    ["existing-model", "model-a"],
    modelOptions,
  );

  assert.deepEqual(visibleSelections, ["existing-model"]);
});

interface SelectionHarnessProps {
  discovery: { modelNames: string[] } | undefined;
  isLoading: boolean;
  error?: Error;
}

function SelectionHarness({ discovery, isLoading, error }: SelectionHarnessProps) {
  const [selectedModelNames, setSelectedModelNames] = React.useState([
    "existing-model",
    "shared-model",
    "model-a",
  ]);
  const modelOptions = React.useMemo(
    () =>
      getProviderModelOptions(
        isLoading || error ? [] : existingModels,
        discovery,
        "OpenAIChatCompletions",
      ),
    [discovery, isLoading, error],
  );

  return React.createElement(
    TooltipProvider,
    null,
    React.createElement(
      "div",
      null,
      React.createElement(
        "output",
        { "aria-label": "selected models" },
        selectedModelNames.join(","),
      ),
      React.createElement(ProviderModelsEditor, {
        idPrefix: "provider-test-",
        providerType: "OpenAIChatCompletions",
        endpoint: "https://models.example/v1",
        hasApiKey: true,
        modelOptions,
        isDiscoveryPending: false,
        fetchModels: () => undefined,
        selectedModelNames,
        onSelectedModelNamesChange: setSelectedModelNames,
        isLoading,
        error,
        onRetry: () => undefined,
      }),
    ),
  );
}
