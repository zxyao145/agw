import assert from "node:assert/strict";
import test, { afterEach, before } from "node:test";

import type { AppearanceSettings } from "@desktop/shared/contracts";
import { DEFAULT_APPEARANCE } from "@desktop/shared/appearance";
import type { AvailableTheme } from "./resolve-theme";

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

/** 迁移后的默认外观，`mode` 为有效 Mode。The default appearance after migration, with the effective mode. */
const INITIAL: AppearanceSettings = { ...DEFAULT_APPEARANCE, mode: "system" };

const THEMES: AvailableTheme[] = [
  {
    id: "soft-theme:soft",
    title: "Soft",
    extensionName: "Soft Theme",
    label: "Soft",
    theme: {
      id: "soft",
      title: "Soft",
      common: {},
      light: { "--background": "#ffffff" },
      dark: {},
    },
  },
  {
    id: "night-themes:night",
    title: "Night",
    extensionName: "Night Themes",
    label: "Night",
    theme: { id: "night", title: "Night", common: {}, light: {}, dark: { "--background": "#000" } },
  },
];

const MANAGE_HREF = "/extensions/?category=themes&returnTo=%2Fdesktop%2Fchat%2F";

function renderForm(appearance: AppearanceSettings, save: Save) {
  const { React } = environment;
  const element = (value: AppearanceSettings) =>
    React.createElement(forms.AppearanceSettingsForm, {
      appearance: value,
      themes: THEMES,
      manageThemesHref: MANAGE_HREF,
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
const themeSelect = () => select("appearance-theme");
const modeButton = (name: string) => environment.screen.getByRole("button", { name });

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
  renderForm(INITIAL, recordingSave().save);
  assert.equal(baseSelect().value, "16");
  assert.equal(chatSelect().value, "follow");
  assert.equal(optionText(chatSelect(), "follow"), "Follow base size (14 px)");
});

test("the size selects offer 12 to 20 for base and 12 to 24 for chat, all enabled", () => {
  renderForm(INITIAL, recordingSave().save);
  assert.deepEqual(optionValues(baseSelect()), [
    "12",
    "13",
    "14",
    "15",
    "16",
    "17",
    "18",
    "19",
    "20",
  ]);
  assert.deepEqual(optionValues(chatSelect()), [
    "follow",
    "12",
    "13",
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

test("choosing a base size saves it with the theme and mode and updates the follow text", async () => {
  const { calls, save } = recordingSave();
  renderForm({ ...INITIAL, theme: "soft-theme:soft", mode: "dark" }, save);
  choose(baseSelect(), "18");
  await environment.waitFor(() => assert.equal(calls.length, 1));
  assert.deepEqual(calls[0], {
    baseFontSize: 18,
    chatFontSize: null,
    theme: "soft-theme:soft",
    mode: "dark",
  });
  assert.equal(optionText(chatSelect(), "follow"), "Follow base size (15.75 px)");
});

test("the reset button of a field saves that field's default", async () => {
  const { calls, save } = recordingSave();
  renderForm({ ...INITIAL, baseFontSize: 18, chatFontSize: 20 }, save);
  environment.fireEvent.click(
    environment.screen.getByRole("button", { name: "Reset chat font size" }),
  );
  await environment.waitFor(() => assert.equal(calls.length, 1));
  assert.deepEqual(calls[0], { ...INITIAL, baseFontSize: 18, chatFontSize: null });
});

test("Reset all saves the default sizes, the Default theme and the System mode", async () => {
  const { calls, save } = recordingSave();
  renderForm({ baseFontSize: 18, chatFontSize: 20, theme: "soft-theme:soft", mode: "dark" }, save);
  environment.fireEvent.click(environment.screen.getByRole("button", { name: "Reset all" }));
  await environment.waitFor(() => assert.equal(calls.length, 1));
  assert.deepEqual(calls[0], {
    baseFontSize: 16,
    chatFontSize: null,
    theme: "default",
    mode: "system",
  });
});

test("a rejected save returns the select to the last saved value", async () => {
  let attempt = 0;
  const attempts: number[] = [];
  const save: Save = async (next) => {
    attempts.push(next.baseFontSize);
    attempt += 1;
    if (attempt === 2) throw new Error("Unable to save");
  };
  const view = renderForm(INITIAL, save);
  choose(baseSelect(), "17");
  await environment.waitFor(() => assert.deepEqual(attempts, [17]));
  view.rerender({ ...INITIAL, baseFontSize: 17 });
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
  const view = renderForm(INITIAL, save);
  choose(baseSelect(), "19");
  await environment.waitFor(() => assert.deepEqual(attempts, [19]));
  view.rerender({ ...INITIAL, baseFontSize: 17 });
  reject(new Error("Unable to save"));
  await environment.waitFor(() => assert.equal(baseSelect().value, "17"));
});

test("an external appearance change updates the selects", () => {
  const view = renderForm(INITIAL, recordingSave().save);
  view.rerender({ ...INITIAL, baseFontSize: 18, chatFontSize: 15, theme: "soft-theme:soft" });
  assert.equal(chatSelect().value, "15");
  assert.equal(baseSelect().value, "18");
  assert.equal(themeSelect().value, "soft-theme:soft");
});

test("a stored value at 12 is selected", () => {
  renderForm({ ...INITIAL, baseFontSize: 12, chatFontSize: 12 }, recordingSave().save);
  assert.equal(baseSelect().value, "12");
  assert.equal(chatSelect().value, "12");
});

test("base size 12 makes the chat follow text 10.5 px", () => {
  renderForm({ ...INITIAL, baseFontSize: 12 }, recordingSave().save);
  assert.equal(optionText(chatSelect(), "follow"), "Follow base size (10.5 px)");
});

test("the form has no document font size select and no document preview", () => {
  renderForm(INITIAL, recordingSave().save);
  assert.equal(document.getElementById("appearance-document-font-size"), null);
  assert.equal(document.querySelector('[data-appearance-preview="document"]'), null);
});

test("the chat preview is an agw-prose container with the compact density", () => {
  renderForm(INITIAL, recordingSave().save);
  const preview = document.querySelector('[data-appearance-preview="compact"]');
  assert.ok(preview);
  assert.ok(preview.classList.contains("agw-prose"));
  assert.equal(preview.getAttribute("data-density"), "compact");
});

test("Visual style comes before the font sizes", () => {
  renderForm(INITIAL, recordingSave().save);
  const headings = Array.from(document.querySelectorAll("h2")).map(
    (heading) => heading.textContent,
  );
  assert.deepEqual(headings, ["Visual style", "Font sizes"]);
});

test("the three mode thumbnails mark the selected mode", () => {
  renderForm({ ...INITIAL, mode: "dark" }, recordingSave().save);
  const buttons = ["System mode", "Light mode", "Dark mode"].map(modeButton);
  assert.deepEqual(
    buttons.map((button) => button.getAttribute("aria-pressed")),
    ["false", "false", "true"],
  );
  const frame = (button: HTMLElement) => button.firstElementChild as HTMLElement;
  assert.ok(frame(buttons[2]).classList.contains("border-primary"));
  assert.equal(frame(buttons[0]).classList.contains("border-primary"), false);
  // System 左半浅色、右半深色。System shows light on the left and dark on the right.
  assert.deepEqual(
    Array.from(frame(buttons[0]).children).map((half) =>
      half.classList.contains("bg-white") ? "light" : "dark",
    ),
    ["light", "dark"],
  );
  assert.equal(frame(buttons[1]).children.length, 1);
  assert.ok(frame(buttons[1]).children[0].classList.contains("bg-white"));
  assert.ok(frame(buttons[2]).children[0].classList.contains("bg-zinc-900"));
});

test("choosing a mode saves it", async () => {
  const { calls, save } = recordingSave();
  renderForm(INITIAL, save);
  environment.fireEvent.click(modeButton("Light mode"));
  await environment.waitFor(() => assert.equal(calls.length, 1));
  assert.deepEqual(calls[0], { ...INITIAL, mode: "light" });
  assert.equal(modeButton("Light mode").getAttribute("aria-pressed"), "true");
});

test("the Theme select lists Default and the available themes", () => {
  renderForm(INITIAL, recordingSave().save);
  assert.deepEqual(optionValues(themeSelect()), [
    "default",
    "soft-theme:soft",
    "night-themes:night",
  ]);
  assert.deepEqual(
    Array.from(themeSelect().options).map((option) => option.textContent),
    ["Default", "Soft", "Night"],
  );
  assert.equal(themeSelect().value, "default");
});

test("choosing a theme saves it with the mode", async () => {
  const { calls, save } = recordingSave();
  renderForm({ ...INITIAL, mode: "light" }, save);
  choose(themeSelect(), "night-themes:night");
  await environment.waitFor(() => assert.equal(calls.length, 1));
  assert.deepEqual(calls[0], { ...INITIAL, mode: "light", theme: "night-themes:night" });
});

test("a rejected theme or mode save returns to the saved value", async () => {
  const save: Save = async () => {
    throw new Error("Unable to save");
  };
  renderForm(INITIAL, save);
  choose(themeSelect(), "soft-theme:soft");
  await environment.waitFor(() => assert.equal(themeSelect().value, "default"));
  environment.fireEvent.click(modeButton("Dark mode"));
  await environment.waitFor(() =>
    assert.equal(modeButton("System mode").getAttribute("aria-pressed"), "true"),
  );
});

test("a selected theme that is not listed is shown by its id", () => {
  renderForm({ ...INITIAL, theme: "gone:theme" }, recordingSave().save);
  assert.equal(themeSelect().value, "gone:theme");
  assert.equal(optionText(themeSelect(), "gone:theme"), "gone:theme");
});

test("Manage opens the theme extensions", () => {
  renderForm(INITIAL, recordingSave().save);
  const manage = environment.screen.getByRole("link", { name: "Manage" });
  // 测试环境没有 Next 的 trailingSlash 配置，Link 去掉结尾斜杠。Without Next's trailingSlash config, Link drops the slash.
  const href = new URL(manage.getAttribute("href")!, "http://localhost");
  assert.equal(href.pathname.replace(/\/$/u, ""), "/extensions");
  assert.equal(href.search, new URL(MANAGE_HREF, "http://localhost").search);
});
