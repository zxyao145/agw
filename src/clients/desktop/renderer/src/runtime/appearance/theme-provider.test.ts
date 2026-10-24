import assert from "node:assert/strict";
import { createRequire } from "node:module";
import test, { afterEach, before } from "node:test";

import type {
  AppearanceSettings,
  ExtensionTheme,
  InstalledExtension,
} from "@desktop/shared/contracts";
import type { ExtensionHost } from "../extensions/extension-host";
import type { DesktopThemeState } from "./theme-provider";

type DomEnvironment = Awaited<ReturnType<typeof import("@agw/test-harness").setupDomEnvironment>>;

// 模块加载时调用 setupDomEnvironment，after 钩子属于整个文件。
// setupDomEnvironment is called at module load so its after hook belongs to the file.
const environmentReady = import("@agw/test-harness").then(({ setupDomEnvironment }) =>
  setupDomEnvironment(),
);

let environment: DomEnvironment;
let themes: typeof import("./theme-provider");
let hosts: typeof import("../extensions/extension-host");
let helper: typeof import("../extensions/extension-host.test-helper");
let nextThemes: typeof import("next-themes");
let sonner: typeof import("sonner");

before(async () => {
  environment = await environmentReady;
  themes = await import("./theme-provider");
  hosts = await import("../extensions/extension-host");
  helper = await import("../extensions/extension-host.test-helper");
  // 被测代码以 CommonJS 引用这两个包，动态 import() 会得到另一份 ESM 构建及其 context；next-themes 在加载时检查
  // window，因此在 DOM 环境建立之后加载。
  // The code under test requires both packages as CommonJS, and a dynamic import() would get the separate ESM build
  // and its context; next-themes checks for window when it loads, so it loads after the DOM environment exists.
  const requireModule = createRequire(__filename);
  nextThemes = requireModule("next-themes");
  sonner = requireModule("sonner");
});

const DARK_QUERY = "(prefers-color-scheme: dark)";

afterEach(() => {
  environment.cleanup();
  localStorage.clear();
  environment.observers.media.setMatches(DARK_QUERY, false);
  document.documentElement.removeAttribute("class");
  document.documentElement.removeAttribute("style");
  document.documentElement.removeAttribute("data-workspace-backdrop");
});

const SOFT: ExtensionTheme = {
  id: "soft",
  title: "Soft",
  common: { "--radius": "0.75rem" },
  light: { "--background": "#ffffff", "--sidebar": "#f9f9f9" },
  dark: { "--background": "#212121" },
};

const WARM: ExtensionTheme = {
  id: "warm",
  title: "Warm",
  common: {},
  light: { "--background": "#fff8f0" },
  dark: { "--background": "#2a2018", "--card": "#30241c" },
};

function themeExtension(enabled = true): InstalledExtension {
  return {
    id: "soft-theme",
    source: "bundled",
    directory: "/extensions/soft-theme",
    manifest: {
      manifestVersion: 1,
      id: "soft-theme",
      name: "Soft Theme",
      version: "1.0.0",
      contributes: {
        themes: [
          { id: "soft", title: "Soft", path: "themes/soft.json" },
          { id: "warm", title: "Warm", path: "themes/warm.json" },
        ],
      },
    },
    validation: "valid",
    errors: [],
    enabled,
    permissionApprovalRequired: false,
    grantedPermissions: [],
    grantedHostPermissions: [],
    lastActivatedVersion: "1.0.0",
    icons: {},
    themes: [SOFT, WARM],
  };
}

const appearance = (overrides: Partial<AppearanceSettings> = {}): AppearanceSettings => ({
  baseFontSize: 16,
  chatFontSize: null,
  theme: "default",
  mode: "light",
  ...overrides,
});

/** 返回的宿主读取 `list()` 作为 extension 清单。The host reads `list()` as the extension list. */
function createHost(list: () => InstalledExtension[]): ExtensionHost {
  const main = helper.createMainProcess(environment.window, {
    installed: themeExtension(),
    storageDirectory: "/data/soft-theme",
    channels: {
      listExtensions: async () => list(),
      releaseExtensionResources: async () => undefined,
    },
  });
  return new hosts.ExtensionHost({
    bridge: main.bridge,
    app: { version: "0.5.0", platform: "darwin", architecture: "arm64" },
    notify: () => undefined,
  });
}

async function startedHost(list: () => InstalledExtension[]): Promise<ExtensionHost> {
  const host = createHost(list);
  await helper.startExtensionHost(host, environment.window);
  return host;
}

type Props = { appearance: AppearanceSettings | null; host: ExtensionHost | null };

function recordingSave() {
  const calls: AppearanceSettings[] = [];
  return { calls, save: async (next: AppearanceSettings) => void calls.push(next) };
}

let current: DesktopThemeState | null = null;

function renderTheme(props: Props, save: (next: AppearanceSettings) => Promise<void>) {
  const { React } = environment;
  function Probe() {
    current = themes.useDesktopTheme();
    return null;
  }
  const element = (value: Props) =>
    React.createElement(nextThemes.ThemeProvider, {
      attribute: "class",
      defaultTheme: "system",
      enableSystem: true,
      disableTransitionOnChange: true,
      children: React.createElement(themes.ThemeProvider, {
        ...value,
        save,
        children: React.createElement(Probe),
      }),
    }) as Parameters<DomEnvironment["render"]>[0];
  const view = environment.render(element(props));
  return {
    rerender: (next: Partial<Props>) => view.rerender(element({ ...props, ...next })),
    unmount: () => view.unmount(),
  };
}

const root = () => document.documentElement;
const variable = (name: string) => root().style.getPropertyValue(name);
const writtenVariables = () => Array.from(root().style).filter((name) => name.startsWith("--"));
const transitionStyles = () =>
  Array.from(document.head.querySelectorAll("style")).filter((style) =>
    style.textContent?.startsWith("*,*::before,*::after{-webkit-transition:none!important"),
  );
const notices = () =>
  sonner.toast.getHistory().map((item) => ("title" in item ? item.title : null));

test("the applied theme is the selected theme's id and title, and Default without one", async () => {
  const host = await startedHost(() => [themeExtension()]);
  const view = renderTheme({ appearance: appearance(), host }, recordingSave().save);
  await environment.waitFor(() => assert.equal(current?.themes.length, 2));
  assert.deepEqual(current?.appliedTheme, { id: "default", title: "Default" });

  view.rerender({ appearance: appearance({ theme: "soft-theme:soft" }) });
  await environment.waitFor(() =>
    assert.deepEqual(current?.appliedTheme, { id: "soft-theme:soft", title: "Soft" }),
  );
});

test("only a theme with a workspace backdrop marks html, in the mode that sets it", async () => {
  const backdrop = "linear-gradient(120deg, #e6e2dd 0 16%, transparent 16.1%)";
  const extension: InstalledExtension = {
    ...themeExtension(),
    themes: [SOFT, { ...WARM, light: { ...WARM.light, "--agw-workspace-backdrop": backdrop } }],
  };
  const host = await startedHost(() => [extension]);
  const marked = () => root().hasAttribute("data-workspace-backdrop");
  const view = renderTheme(
    { appearance: appearance({ theme: "soft-theme:warm" }), host },
    recordingSave().save,
  );
  await environment.waitFor(() => assert.equal(variable("--agw-workspace-backdrop"), backdrop));
  assert.equal(marked(), true);

  view.rerender({ appearance: appearance({ theme: "soft-theme:warm", mode: "dark" }) });
  await environment.waitFor(() => assert.equal(variable("--background"), "#2a2018"));
  assert.equal(marked(), false);

  view.rerender({ appearance: appearance({ theme: "soft-theme:warm" }) });
  await environment.waitFor(() => assert.equal(marked(), true));
  view.rerender({ appearance: appearance({ theme: "soft-theme:soft" }) });
  await environment.waitFor(() => assert.equal(variable("--background"), "#ffffff"));
  assert.equal(marked(), false);

  view.rerender({ appearance: appearance({ theme: "soft-theme:warm" }) });
  await environment.waitFor(() => assert.equal(marked(), true));
  view.rerender({ appearance: appearance() });
  await environment.waitFor(() => assert.deepEqual(writtenVariables(), []));
  assert.equal(marked(), false);

  view.rerender({ appearance: appearance({ theme: "soft-theme:warm" }) });
  await environment.waitFor(() => assert.equal(marked(), true));
  view.unmount();
  assert.equal(marked(), false);
});

test("Default writes no theme variables", async () => {
  const host = await startedHost(() => [themeExtension()]);
  renderTheme({ appearance: appearance(), host }, recordingSave().save);
  await environment.waitFor(() => assert.equal(current?.themes.length, 2));
  assert.deepEqual(writtenVariables(), []);
  assert.deepEqual(current?.appliedTokens, {});
});

test("Soft writes its light variables, and its dark ones in dark mode", async () => {
  const host = await startedHost(() => [themeExtension()]);
  const view = renderTheme(
    { appearance: appearance({ theme: "soft-theme:soft" }), host },
    recordingSave().save,
  );
  await environment.waitFor(() => assert.equal(variable("--background"), "#ffffff"));
  assert.deepEqual(writtenVariables().sort(), ["--background", "--radius", "--sidebar"]);
  assert.equal(variable("--sidebar"), "#f9f9f9");
  assert.equal(variable("--radius"), "0.75rem");
  assert.equal(root().classList.contains("dark"), false);

  view.rerender({ appearance: appearance({ theme: "soft-theme:soft", mode: "dark" }) });
  await environment.waitFor(() => assert.equal(variable("--background"), "#212121"));
  assert.ok(root().classList.contains("dark"));
  assert.deepEqual(writtenVariables().sort(), ["--background", "--radius"]);
});

test("switching themes removes the previous variables and Default removes all", async () => {
  const host = await startedHost(() => [themeExtension()]);
  const view = renderTheme(
    { appearance: appearance({ theme: "soft-theme:soft" }), host },
    recordingSave().save,
  );
  await environment.waitFor(() => assert.equal(variable("--sidebar"), "#f9f9f9"));

  view.rerender({ appearance: appearance({ theme: "soft-theme:warm" }) });
  await environment.waitFor(() => assert.equal(variable("--background"), "#fff8f0"));
  assert.deepEqual(writtenVariables(), ["--background"]);

  view.rerender({ appearance: appearance({ theme: "default" }) });
  await environment.waitFor(() => assert.deepEqual(writtenVariables(), []));
});

test("a system light or dark change rewrites the variables of the System mode", async () => {
  const host = await startedHost(() => [themeExtension()]);
  renderTheme(
    { appearance: appearance({ theme: "soft-theme:warm", mode: "system" }), host },
    recordingSave().save,
  );
  await environment.waitFor(() => assert.equal(variable("--background"), "#fff8f0"));
  assert.equal(current?.kind, "light");

  await environment.act(async () => environment.observers.media.setMatches(DARK_QUERY, true));
  await environment.waitFor(() => assert.equal(variable("--background"), "#2a2018"));
  assert.equal(variable("--card"), "#30241c");
  assert.equal(current?.kind, "dark");

  await environment.act(async () => environment.observers.media.setMatches(DARK_QUERY, false));
  await environment.waitFor(() => assert.equal(variable("--card"), ""));
  assert.equal(variable("--background"), "#fff8f0");
});

test("variables change while a style disables transitions, and the style is removed afterwards", async () => {
  const host = await startedHost(() => [themeExtension()]);
  const view = renderTheme({ appearance: appearance(), host }, recordingSave().save);
  await environment.waitFor(() => assert.equal(current?.themes.length, 2));
  await environment.waitFor(() => assert.deepEqual(transitionStyles(), []));

  view.rerender({ appearance: appearance({ theme: "soft-theme:soft" }) });
  assert.equal(variable("--background"), "#ffffff");
  assert.equal(transitionStyles().length, 1);
  await environment.waitFor(() => assert.deepEqual(transitionStyles(), []));

  view.rerender({ appearance: appearance({ theme: "default" }) });
  assert.deepEqual(writtenVariables(), []);
  assert.equal(transitionStyles().length, 1);
  await environment.waitFor(() => assert.deepEqual(transitionStyles(), []));
});

test("unmounting removes the written variables", async () => {
  const host = await startedHost(() => [themeExtension()]);
  const view = renderTheme(
    { appearance: appearance({ theme: "soft-theme:soft" }), host },
    recordingSave().save,
  );
  await environment.waitFor(() => assert.equal(variable("--background"), "#ffffff"));
  view.unmount();
  assert.deepEqual(writtenVariables(), []);
});

test("availability is not judged before the first extension state arrives", async () => {
  let list = [themeExtension(false)];
  const host = createHost(() => list);
  const { calls, save } = recordingSave();
  renderTheme({ appearance: appearance({ theme: "soft-theme:soft" }), host }, save);
  await new Promise((resolve) => setTimeout(resolve, 20));
  assert.deepEqual(calls, []);
  assert.deepEqual(writtenVariables(), []);

  list = [themeExtension(true)];
  await environment.act(() => helper.startExtensionHost(host, environment.window));
  await environment.waitFor(() => assert.equal(variable("--background"), "#ffffff"));
  assert.deepEqual(calls, []);
});

test("a selected theme of a disabled extension falls back to Default with a notice", async () => {
  let list = [themeExtension()];
  const host = await startedHost(() => list);
  const { calls, save } = recordingSave();
  renderTheme({ appearance: appearance({ theme: "soft-theme:soft", mode: "dark" }), host }, save);
  await environment.waitFor(() => assert.equal(variable("--background"), "#212121"));

  list = [themeExtension(false)];
  await environment.act(() => host.rescan());
  await environment.waitFor(() =>
    assert.deepEqual(calls, [appearance({ theme: "default", mode: "dark" })]),
  );
  assert.ok(notices().includes("Soft is no longer available. Using Default."));
  assert.deepEqual(writtenVariables(), []);
});

test("a selected theme that no extension provides falls back to Default and names its id", async () => {
  const host = await startedHost(() => []);
  const { calls, save } = recordingSave();
  renderTheme({ appearance: appearance({ theme: "gone-theme:night" }), host }, save);
  await environment.waitFor(() => assert.deepEqual(calls, [appearance({ theme: "default" })]));
  assert.ok(notices().includes("gone-theme:night is no longer available. Using Default."));
});

test("a removed extension's theme is named by the title seen before", async () => {
  let list = [themeExtension()];
  const host = await startedHost(() => list);
  const { calls, save } = recordingSave();
  renderTheme({ appearance: appearance({ theme: "soft-theme:warm" }), host }, save);
  await environment.waitFor(() => assert.equal(variable("--background"), "#fff8f0"));

  list = [];
  await environment.act(() => host.rescan());
  await environment.waitFor(() => assert.deepEqual(calls, [appearance({ theme: "default" })]));
  assert.ok(notices().includes("Warm is no longer available. Using Default."));
});

test("settings without a mode migrate the next-themes value and keep it while the save is pending", async () => {
  localStorage.setItem("theme", "dark");
  const host = await startedHost(() => [themeExtension()]);
  const calls: AppearanceSettings[] = [];
  const save = (next: AppearanceSettings) => {
    calls.push(next);
    return new Promise<void>(() => undefined);
  };
  const stored = { baseFontSize: 16, chatFontSize: null, theme: "default" };
  renderTheme({ appearance: stored, host }, save);
  await environment.waitFor(() => assert.deepEqual(calls, [{ ...stored, mode: "dark" }]));
  assert.ok(root().classList.contains("dark"));
  assert.equal(current?.mode, "dark");
  assert.deepEqual(current?.appearance, { ...stored, mode: "dark" });

  // 迁移保存完成之前修改字号：保存的外观中 mode 为有效 Mode。A size change before the migration finishes saves the effective mode.
  void current!.saveAppearance({ ...current!.appearance!, baseFontSize: 18 });
  assert.deepEqual(calls[1], { ...stored, baseFontSize: 18, mode: "dark" });
});

test("settings without a mode migrate System when next-themes has no value", async () => {
  const host = await startedHost(() => [themeExtension()]);
  const { calls, save } = recordingSave();
  const stored = { baseFontSize: 16, chatFontSize: null, theme: "default" };
  renderTheme({ appearance: stored, host }, save);
  await environment.waitFor(() => assert.deepEqual(calls, [{ ...stored, mode: "system" }]));
});

test("the stored mode wins over the next-themes value", async () => {
  localStorage.setItem("theme", "dark");
  const host = await startedHost(() => [themeExtension()]);
  const { calls, save } = recordingSave();
  renderTheme({ appearance: appearance({ mode: "light" }), host }, save);
  await environment.waitFor(() => assert.equal(localStorage.getItem("theme"), "light"));
  assert.equal(root().classList.contains("dark"), false);
  assert.deepEqual(calls, []);
});

test("setMode saves the mode before next-themes applies it", async () => {
  const host = await startedHost(() => [themeExtension()]);
  let finish: () => void = () => undefined;
  const calls: AppearanceSettings[] = [];
  // 与 DesktopRuntimeProvider 相同：保存完成时设置已经更新。Like DesktopRuntimeProvider, the settings update when the save finishes.
  const save = (next: AppearanceSettings) => {
    calls.push(next);
    return new Promise<void>((resolve) => {
      finish = () => {
        view.rerender({ appearance: next });
        resolve();
      };
    });
  };
  const view = renderTheme({ appearance: appearance({ mode: "light" }), host }, save);
  await environment.waitFor(() => assert.equal(current?.kind, "light"));

  let done = false;
  void current!.setMode("dark").then(() => (done = true));
  assert.deepEqual(calls, [appearance({ mode: "dark" })]);
  await new Promise((resolve) => setTimeout(resolve, 20));
  assert.equal(root().classList.contains("dark"), false);

  await environment.act(async () => finish());
  await environment.waitFor(() => assert.ok(done));
  await environment.waitFor(() => assert.ok(root().classList.contains("dark")));
  assert.equal(localStorage.getItem("theme"), "dark");
});

test("without settings setMode only changes next-themes", async () => {
  const { calls, save } = recordingSave();
  renderTheme({ appearance: null, host: null }, save);
  await environment.act(() => current!.setMode("dark"));
  await environment.waitFor(() => assert.ok(root().classList.contains("dark")));
  assert.deepEqual(calls, []);
});
