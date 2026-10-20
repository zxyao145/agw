import assert from "node:assert/strict";
import test from "node:test";

import { createExtensionError } from "@agw/extension-sdk";
import { JSDOM } from "jsdom";
import { act, createElement } from "react";

// React 在 jsdom 的 document 中渲染，`act` 等待状态更新完成。React renders into jsdom's document; `act` awaits updates.
const { window } = new JSDOM("<!doctype html><html><body></body></html>");
Object.assign(globalThis, { window, document: window.document, IS_REACT_ACT_ENVIRONMENT: true });

const { createRoot } = await import("react-dom/client");
const { DocumentError } = await import("./document-error");
type TextEncodingOption = import("./document-error").TextEncodingOption;

const ENCODINGS: TextEncodingOption[] = [
  { id: "utf-8", label: "Unicode (UTF-8)" },
  { id: "gb18030", label: "Chinese Simplified (GB18030)" },
  { id: "windows-1252", label: "Western European (Windows 1252)" },
];

type Rendered = {
  container: HTMLElement;
  listCalls: () => number;
  reopened: string[];
  unmount(): Promise<void>;
};

async function renderPanel(
  error: unknown,
  listEncodings: () => Promise<readonly TextEncodingOption[]> = async () => ENCODINGS,
): Promise<Rendered> {
  const container = document.createElement("div");
  document.body.append(container);
  const root = createRoot(container);
  const reopened: string[] = [];
  let calls = 0;
  await act(async () => {
    root.render(
      createElement(DocumentError, {
        fileName: "notes.md",
        error,
        listEncodings: () => {
          calls += 1;
          return listEncodings();
        },
        onReopenWithEncoding: (encoding: string) => reopened.push(encoding),
      }),
    );
  });
  return {
    container,
    listCalls: () => calls,
    reopened,
    async unmount() {
      await act(async () => root.unmount());
      container.remove();
    },
  };
}

function buttonTexts(container: HTMLElement): string[] {
  return [...container.querySelectorAll("button")].map((button) => button.textContent ?? "");
}

async function click(container: HTMLElement, text: string): Promise<void> {
  const button = [...container.querySelectorAll("button")].find(
    (candidate) => candidate.textContent === text,
  );
  assert.ok(button, `No button "${text}" in: ${buttonTexts(container).join(", ")}`);
  await act(async () => button.click());
}

test("a binary file shows the explanation without Reopen with Encoding…", async () => {
  const panel = await renderPanel(createExtensionError("ERR_NOT_TEXT", "NUL bytes."));
  assert.equal(
    panel.container.querySelector('[role="alert"]')?.textContent,
    "Could not read notes.mdThis file is binary, so it can't be edited as text.",
  );
  assert.deepEqual(buttonTexts(panel.container), []);
  await panel.unmount();
});

test("other errors show their message without Reopen with Encoding…", async () => {
  const panel = await renderPanel(createExtensionError("ENOENT", "notes.md does not exist."));
  assert.equal(
    panel.container.querySelector('[role="alert"]')?.textContent,
    "Could not read notes.mdnotes.md does not exist.",
  );
  assert.deepEqual(buttonTexts(panel.container), []);
  await panel.unmount();
});

test("an unknown encoding offers the encoding list and reopens with the chosen encoding", async () => {
  const panel = await renderPanel(createExtensionError("ERR_UNKNOWN_ENCODING", "Unknown."));
  assert.equal(
    panel.container.querySelector('[role="alert"]')?.textContent,
    "Could not read notes.mdThe encoding of this file couldn't be detected.",
  );
  assert.deepEqual(buttonTexts(panel.container), ["Reopen with Encoding…"]);
  assert.equal(panel.listCalls(), 0);

  await click(panel.container, "Reopen with Encoding…");
  assert.equal(panel.listCalls(), 1);
  assert.deepEqual(buttonTexts(panel.container), [
    "Unicode (UTF-8)",
    "Chinese Simplified (GB18030)",
    "Western European (Windows 1252)",
    "Cancel",
  ]);
  assert.deepEqual(
    [...panel.container.querySelectorAll("[data-encoding]")].map((button) =>
      button.getAttribute("data-encoding"),
    ),
    ["utf-8", "gb18030", "windows-1252"],
  );

  await click(panel.container, "Cancel");
  assert.deepEqual(buttonTexts(panel.container), ["Reopen with Encoding…"]);
  assert.deepEqual(panel.reopened, []);

  await click(panel.container, "Reopen with Encoding…");
  await click(panel.container, "Western European (Windows 1252)");
  assert.deepEqual(panel.reopened, ["windows-1252"]);
  assert.equal(panel.listCalls(), 2);
  await panel.unmount();
});

test("a failure to list the encodings is shown", async () => {
  const panel = await renderPanel(
    createExtensionError("ERR_UNKNOWN_ENCODING", "Unknown."),
    async () => {
      throw createExtensionError("ERR_CONNECTION_CLOSED", "The connection is closed.");
    },
  );
  await click(panel.container, "Reopen with Encoding…");
  assert.deepEqual(buttonTexts(panel.container), []);
  assert.equal(
    panel.container.querySelector(".document-error-note")?.textContent,
    "The connection is closed.",
  );
  assert.deepEqual(panel.reopened, []);
  await panel.unmount();
});
