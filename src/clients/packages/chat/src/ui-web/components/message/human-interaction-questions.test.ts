import assert from "node:assert/strict";
import { existsSync } from "node:fs";
import { createRequire } from "node:module";
import test, { after, afterEach } from "node:test";
import { fileURLToPath } from "node:url";
import { JSDOM } from "jsdom";
import * as React from "react";

const testRequire = createRequire(import.meta.url);
const componentsRoot = fileURLToPath(new URL("../../../../../components", import.meta.url));
const moduleCache = testRequire.cache as Record<
  string,
  { exports: unknown; id: string; filename: string; loaded: boolean }
>;

// Workspace peer hoisting gives @agw/components its own React copy. Reuse the
// test package's copy so React DOM, Radix, and the component share one hook dispatcher.
for (const specifier of ["react", "react/jsx-runtime", "react/jsx-dev-runtime", "react-dom"]) {
  const sharedModulePath = testRequire.resolve(specifier);
  const componentsModulePath = testRequire.resolve(specifier, { paths: [componentsRoot] });
  assert.ok(
    existsSync(sharedModulePath),
    `Expected the test React module for '${specifier}' to exist at ${sharedModulePath}.`,
  );
  assert.ok(
    existsSync(componentsModulePath),
    `Expected the @agw/components React module for '${specifier}' to exist at ${componentsModulePath}.`,
  );
  assert.notEqual(
    componentsModulePath,
    sharedModulePath,
    `Expected workspace peer copies for '${specifier}' to resolve separately; hoisting layout changed.`,
  );
  moduleCache[componentsModulePath] = {
    exports: testRequire(sharedModulePath),
    id: componentsModulePath,
    filename: componentsModulePath,
    loaded: true,
  };
}

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
  KeyboardEvent: window.KeyboardEvent,
  MutationObserver: window.MutationObserver,
  getComputedStyle: window.getComputedStyle.bind(window),
  requestAnimationFrame: window.requestAnimationFrame.bind(window),
  cancelAnimationFrame: window.cancelAnimationFrame.bind(window),
})) {
  Object.defineProperty(globalThis, name, { configurable: true, value });
}

Object.defineProperty(window, "matchMedia", {
  configurable: true,
  value: (query: string) => ({
    matches: false,
    media: query,
    onchange: null,
    addListener() {},
    removeListener() {},
    addEventListener() {},
    removeEventListener() {},
    dispatchEvent() {
      return false;
    },
  }),
});

(globalThis as typeof globalThis & { IS_REACT_ACT_ENVIRONMENT: boolean }).IS_REACT_ACT_ENVIRONMENT =
  true;

const { cleanup, fireEvent, render, screen } = await import("@testing-library/react");
const { HumanInteractionQuestions } = await import("./human-interaction-questions.tsx");

afterEach(() => cleanup());
after(() => dom.window.close());

const request = {
  requestType: "human-interaction" as const,
  requestId: "request-1",
  mode: "interaction",
  prompt: "Choose one.",
  questions: [
    {
      question: "Which approach?",
      header: "Approach",
      multiSelect: false,
      options: [
        {
          label: "Preview option",
          description: "This option has a preview.",
          preview: "## Preview content\n\nThe preview remains inside its panel.",
        },
        {
          label: "No preview option",
          description: "This option does not have a preview.",
        },
      ],
    },
  ],
};

function renderQuestions(requestToRender = request) {
  return render(
    React.createElement(HumanInteractionQuestions, {
      request: requestToRender,
      onSubmit: () => {},
      onCancel: () => {},
    }),
  );
}

test("hovering an option without a preview keeps the preview region mounted", () => {
  renderQuestions();

  const previewRegion = screen.getByRole("region", { name: "Option preview" });
  const noPreviewOption = screen.getByText("No preview option").closest("label");
  assert.ok(noPreviewOption);
  assert.ok(screen.getByText("Hover or focus an option to preview it."));

  fireEvent.mouseEnter(noPreviewOption);

  assert.ok(screen.getByText("No preview is available for this option."));
  assert.strictEqual(screen.getByRole("region", { name: "Option preview" }), previewRegion);
});

test("questions without previews do not reserve a preview region", () => {
  renderQuestions({
    ...request,
    questions: [
      {
        ...request.questions[0]!,
        options: request.questions[0]!.options.map(({ label, description }) => ({
          label,
          description,
        })),
      },
    ],
  });

  assert.equal(screen.queryByRole("region", { name: "Option preview" }), null);
});

test("focusing an option with a preview updates content without replacing the region", () => {
  renderQuestions();

  const previewRegion = screen.getByRole("region", { name: "Option preview" });
  const previewOption = screen.getByRole("radio", { name: /^Preview option/ });

  fireEvent.focus(previewOption);

  assert.ok(screen.getByText("Preview content"));
  assert.strictEqual(screen.getByRole("region", { name: "Option preview" }), previewRegion);
});
