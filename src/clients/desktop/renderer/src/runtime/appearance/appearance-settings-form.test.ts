import assert from "node:assert/strict";
import test, { afterEach, before } from "node:test";

import type { AppearanceSettings } from "@desktop/shared/contracts";
import { DEFAULT_APPEARANCE } from "@desktop/shared/appearance";

type DomEnvironment = Awaited<ReturnType<typeof import("@agw/test-harness").setupDomEnvironment>>;
type FormModule = typeof import("./appearance-settings-form");

// 模块加载时调用 setupDomEnvironment，after 钩子属于整个文件。
// setupDomEnvironment is called at module load so its after hook belongs to the file.
const environmentReady = import("@agw/test-harness").then(({ setupDomEnvironment }) =>
  setupDomEnvironment(),
);

let environment: DomEnvironment;
let forms: FormModule;

before(async () => {
  environment = await environmentReady;
  forms = await import("./appearance-settings-form");
});
afterEach(() => environment.cleanup());

type Save = (next: AppearanceSettings) => Promise<void>;

function renderForm(appearance: AppearanceSettings, save: Save) {
  const { React } = environment;
  const element = (value: AppearanceSettings) =>
    React.createElement(forms.AppearanceSettingsForm, {
      appearance: value,
      save,
    }) as Parameters<DomEnvironment["render"]>[0];
  const result = environment.render(element(appearance));
  return { rerender: (next: AppearanceSettings) => result.rerender(element(next)) };
}

function select(id: string): HTMLSelectElement {
  const element = document.getElementById(id);
  assert.ok(element instanceof HTMLSelectElement, `Missing select #${id}`);
  return element;
}

const baseSelect = () => select("appearance-base-font-size");
const chatSelect = () => select("appearance-chat-font-size");

function optionText(element: HTMLSelectElement, value: string): string {
  const option = Array.from(element.options).find((candidate) => candidate.value === value);
  assert.ok(option, `Missing option ${value} in #${element.id}`);
  return option.textContent ?? "";
}

function optionValues(element: HTMLSelectElement): string[] {
  return Array.from(element.options).map((option) => option.value);
}

function choose(element: HTMLSelectElement, value: string): void {
  environment.fireEvent.change(element, { target: { value } });
}

function recordingSave() {
  const calls: AppearanceSettings[] = [];
  const save: Save = async (next) => {
    calls.push(next);
  };
  return { calls, save };
}

test("defaults show base 16 and follow for chat", () => {
  renderForm(DEFAULT_APPEARANCE, recordingSave().save);
  assert.equal(baseSelect().value, "16");
  assert.equal(chatSelect().value, "follow");
  assert.equal(optionText(chatSelect(), "follow"), "Follow base size (14 px)");
});

test("the size selects offer 14 to 20 for base and 14 to 24 for chat, all enabled", () => {
  renderForm(DEFAULT_APPEARANCE, recordingSave().save);
  assert.deepEqual(optionValues(baseSelect()), ["14", "15", "16", "17", "18", "19", "20"]);
  assert.deepEqual(optionValues(chatSelect()), [
    "follow",
    "14",
    "15",
    "16",
    "17",
    "18",
    "19",
    "20",
    "21",
    "22",
    "23",
    "24",
  ]);
  for (const element of [baseSelect(), chatSelect()]) {
    assert.equal(
      Array.from(element.options).some((option) => option.disabled),
      false,
    );
  }
});

test("the form has no minimum font size select", () => {
  renderForm(DEFAULT_APPEARANCE, recordingSave().save);
  assert.equal(document.getElementById("appearance-min-font-size"), null);
  assert.equal(document.body.textContent?.includes("Minimum font size"), false);
});

test("choosing a base size saves it and updates the follow text", async () => {
  const { calls, save } = recordingSave();
  renderForm(DEFAULT_APPEARANCE, save);
  choose(baseSelect(), "18");
  await environment.waitFor(() => assert.equal(calls.length, 1));
  assert.deepEqual(calls[0], {
    baseFontSize: 18,
    chatFontSize: null,
  });
  assert.equal(optionText(chatSelect(), "follow"), "Follow base size (15.75 px)");
});

test("the reset button of a field saves that field's default", async () => {
  const { calls, save } = recordingSave();
  renderForm({ baseFontSize: 18, chatFontSize: 20 }, save);
  environment.fireEvent.click(
    environment.screen.getByRole("button", { name: "Reset chat font size" }),
  );
  await environment.waitFor(() => assert.equal(calls.length, 1));
  assert.deepEqual(calls[0], {
    baseFontSize: 18,
    chatFontSize: null,
  });
});

test("Reset all saves the default appearance", async () => {
  const { calls, save } = recordingSave();
  renderForm({ baseFontSize: 18, chatFontSize: 20 }, save);
  environment.fireEvent.click(environment.screen.getByRole("button", { name: "Reset all" }));
  await environment.waitFor(() => assert.equal(calls.length, 1));
  assert.deepEqual(calls[0], DEFAULT_APPEARANCE);
});

test("a rejected save returns the select to the last saved value", async () => {
  let attempt = 0;
  const attempts: number[] = [];
  const save: Save = async (next) => {
    attempts.push(next.baseFontSize);
    attempt += 1;
    if (attempt === 2) throw new Error("Unable to save");
  };
  const view = renderForm(DEFAULT_APPEARANCE, save);
  choose(baseSelect(), "17");
  await environment.waitFor(() => assert.deepEqual(attempts, [17]));
  view.rerender({ ...DEFAULT_APPEARANCE, baseFontSize: 17 });
  assert.equal(baseSelect().value, "17");

  choose(baseSelect(), "19");
  await environment.waitFor(() => assert.deepEqual(attempts, [17, 19]));
  await environment.waitFor(() => assert.equal(baseSelect().value, "17"));
});

test("a rejected save returns to the prop that arrived while the save was pending", async () => {
  let reject: (error: Error) => void = () => {};
  const pending = new Promise<void>((_, fail) => {
    reject = fail;
  });
  const attempts: number[] = [];
  const save: Save = (next) => {
    attempts.push(next.baseFontSize);
    return pending;
  };
  const view = renderForm(DEFAULT_APPEARANCE, save);
  choose(baseSelect(), "19");
  await environment.waitFor(() => assert.deepEqual(attempts, [19]));
  view.rerender({ ...DEFAULT_APPEARANCE, baseFontSize: 17 });
  reject(new Error("Unable to save"));
  await environment.waitFor(() => assert.equal(baseSelect().value, "17"));
});

test("an external appearance change updates the selects", () => {
  const view = renderForm(DEFAULT_APPEARANCE, recordingSave().save);
  view.rerender({ baseFontSize: 18, chatFontSize: 15 });
  assert.equal(chatSelect().value, "15");
  assert.equal(baseSelect().value, "18");
});

test("a stored value at 14 is selected", () => {
  renderForm({ baseFontSize: 14, chatFontSize: 14 }, recordingSave().save);
  assert.equal(baseSelect().value, "14");
  assert.equal(chatSelect().value, "14");
});

test("the form has no document font size select and no document preview", () => {
  renderForm(DEFAULT_APPEARANCE, recordingSave().save);
  assert.equal(document.getElementById("appearance-document-font-size"), null);
  assert.equal(document.querySelector('[data-appearance-preview="document"]'), null);
});

test("the chat preview is an agw-prose container with the compact density", () => {
  renderForm(DEFAULT_APPEARANCE, recordingSave().save);
  const preview = document.querySelector('[data-appearance-preview="compact"]');
  assert.ok(preview);
  assert.ok(preview.classList.contains("agw-prose"));
  assert.equal(preview.getAttribute("data-density"), "compact");
});
