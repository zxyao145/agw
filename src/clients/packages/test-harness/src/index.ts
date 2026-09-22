import { existsSync, readdirSync } from "node:fs";
import { createRequire, registerHooks } from "node:module";
import { join } from "node:path";
import { after, afterEach } from "node:test";
import { fileURLToPath } from "node:url";
import { JSDOM } from "jsdom";
import type * as ReactNamespace from "react";
import type * as TestingLibrary from "@testing-library/react";

import { installBrowserApis, type ObserverRegistry } from "./browser-apis";

export { installLayoutMetrics } from "./browser-apis";
export type { LayoutMetrics, ObserverRegistry } from "./browser-apis";
export { startApiServer } from "./api-server";
export type { ApiRequestRecord, ApiRouteHandler, ApiRoutes, ApiServer } from "./api-server";

// Each workspace installs its own React copy under a hoisted node_modules, so a component
// imported from another package would otherwise run on a second hook dispatcher.
// 每个工作区在各自的 node_modules 下安装独立的 React 副本，跨包导入的组件否则会运行在第二套 hook 调度器上。
const SHARED_MODULES = [
  "react",
  "react/jsx-runtime",
  "react/jsx-dev-runtime",
  "react-dom",
  "react-dom/client",
] as const;

const harnessRequire = createRequire(import.meta.url);
const clientsRoot = fileURLToPath(new URL("../../../", import.meta.url));

let reactInstancesShared = false;

function workspaceDirectories(): string[] {
  const packagesRoot = join(clientsRoot, "packages");
  const packageDirectories = existsSync(packagesRoot)
    ? readdirSync(packagesRoot).map((entry) => join(packagesRoot, entry))
    : [];
  return [...packageDirectories, join(clientsRoot, "web"), join(clientsRoot, "desktop")].filter(
    (directory) => existsSync(join(directory, "package.json")),
  );
}

function shareReactInstances(): void {
  if (reactInstancesShared) return;
  reactInstancesShared = true;

  const sharedEsmUrls: Record<string, string> = {};
  for (const specifier of SHARED_MODULES) {
    sharedEsmUrls[specifier] = import.meta.resolve(specifier);
  }
  registerHooks({
    resolve(specifier, context, nextResolve) {
      const url = sharedEsmUrls[specifier];
      return url ? { url, shortCircuit: true } : nextResolve(specifier, context);
    },
  });

  const moduleCache = harnessRequire.cache as Record<
    string,
    { exports: unknown; id: string; filename: string; loaded: boolean }
  >;
  for (const directory of workspaceDirectories()) {
    for (const specifier of SHARED_MODULES) {
      let workspaceModulePath: string;
      let sharedModulePath: string;
      try {
        workspaceModulePath = harnessRequire.resolve(specifier, { paths: [directory] });
        sharedModulePath = harnessRequire.resolve(specifier);
      } catch {
        continue;
      }
      if (workspaceModulePath === sharedModulePath) continue;
      moduleCache[workspaceModulePath] = {
        exports: harnessRequire(sharedModulePath),
        id: workspaceModulePath,
        filename: workspaceModulePath,
        loaded: true,
      };
    }
  }
}

export type DomEnvironmentOptions = {
  html?: string;
  url?: string;
};

export type DomEnvironment = {
  window: JSDOM["window"];
  observers: ObserverRegistry;
  React: typeof ReactNamespace;
  act: typeof TestingLibrary.act;
  cleanup: typeof TestingLibrary.cleanup;
  createEvent: typeof TestingLibrary.createEvent;
  fireEvent: typeof TestingLibrary.fireEvent;
  render: typeof TestingLibrary.render;
  screen: typeof TestingLibrary.screen;
  waitFor: typeof TestingLibrary.waitFor;
  within: typeof TestingLibrary.within;
};

/**
 * Builds the DOM environment a rendering test needs and returns the React instance every
 * component in the process shares. Call it once per test file, before importing any component.
 * 建立渲染测试所需的 DOM 环境，并返回进程内所有组件共享的 React 实例。
 * 每个测试文件调用一次，且必须在导入任何组件之前调用。
 */
export async function setupDomEnvironment(
  options: DomEnvironmentOptions = {},
): Promise<DomEnvironment> {
  const dom = new JSDOM(options.html ?? "<!doctype html><html><body></body></html>", {
    pretendToBeVisual: true,
    url: options.url ?? "http://localhost/",
  });
  const { window } = dom;
  const observers = installBrowserApis(
    window as unknown as Parameters<typeof installBrowserApis>[0],
  );

  for (const [name, value] of Object.entries({
    window,
    document: window.document,
    navigator: window.navigator,
    location: window.location,
    history: window.history,
    localStorage: window.localStorage,
    sessionStorage: window.sessionStorage,
    Element: window.Element,
    HTMLElement: window.HTMLElement,
    HTMLAnchorElement: window.HTMLAnchorElement,
    HTMLButtonElement: window.HTMLButtonElement,
    HTMLInputElement: window.HTMLInputElement,
    HTMLTextAreaElement: window.HTMLTextAreaElement,
    HTMLSelectElement: window.HTMLSelectElement,
    HTMLFormElement: window.HTMLFormElement,
    SVGElement: window.SVGElement,
    Node: window.Node,
    NodeList: window.NodeList,
    NodeFilter: window.NodeFilter,
    HTMLCollection: window.HTMLCollection,
    DOMTokenList: window.DOMTokenList,
    DocumentFragment: window.DocumentFragment,
    Range: window.Range,
    Text: window.Text,
    Comment: window.Comment,
    Blob: window.Blob,
    File: window.File,
    FileList: window.FileList,
    FileReader: window.FileReader,
    Image: window.Image,
    XMLSerializer: window.XMLSerializer,
    Event: window.Event,
    CustomEvent: window.CustomEvent,
    MouseEvent: window.MouseEvent,
    PointerEvent: window.PointerEvent,
    KeyboardEvent: window.KeyboardEvent,
    FocusEvent: window.FocusEvent,
    InputEvent: window.InputEvent,
    DOMParser: window.DOMParser,
    MutationObserver: window.MutationObserver,
    ResizeObserver: window.ResizeObserver,
    IntersectionObserver: window.IntersectionObserver,
    matchMedia: window.matchMedia.bind(window),
    getComputedStyle: window.getComputedStyle.bind(window),
    requestAnimationFrame: window.requestAnimationFrame.bind(window),
    cancelAnimationFrame: window.cancelAnimationFrame.bind(window),
    scrollTo: () => {},
  })) {
    Object.defineProperty(globalThis, name, { configurable: true, value });
  }

  (
    globalThis as typeof globalThis & { IS_REACT_ACT_ENVIRONMENT: boolean }
  ).IS_REACT_ACT_ENVIRONMENT = true;

  // React DOM reads the browser globals when it first loads, so share the instances only
  // once the environment above exists.
  // React DOM 在首次加载时读取浏览器全局对象，因此在上述环境建立之后再共享实例。
  shareReactInstances();

  const React = await import("react");
  // tsx compiles workspace .tsx sources with the classic JSX transform, which emits
  // React.createElement without adding an import.
  // tsx 以 classic JSX 转换编译工作区的 .tsx 源码，生成的 React.createElement 不会自带导入。
  Object.defineProperty(globalThis, "React", { configurable: true, value: React });

  const testingLibrary = await import("@testing-library/react");

  afterEach(() => {
    testingLibrary.cleanup();
    // Testing Library restores the flag to its pre-render value; keep it on so late
    // asynchronous updates from a popup or a query still run inside an act environment.
    // Testing Library 会把该标志恢复到渲染前的值；保持开启，使弹出层或查询的迟到异步更新仍在 act 环境内运行。
    (
      globalThis as typeof globalThis & { IS_REACT_ACT_ENVIRONMENT: boolean }
    ).IS_REACT_ACT_ENVIRONMENT = true;
  });
  after(() => window.close());

  return {
    window,
    observers,
    React,
    act: testingLibrary.act,
    cleanup: testingLibrary.cleanup,
    createEvent: testingLibrary.createEvent,
    fireEvent: testingLibrary.fireEvent,
    render: testingLibrary.render,
    screen: testingLibrary.screen,
    waitFor: testingLibrary.waitFor,
    within: testingLibrary.within,
  };
}
