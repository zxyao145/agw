import { after, afterEach } from "node:test";
import { JSDOM } from "jsdom";
import type * as ReactNamespace from "react";
import type * as TestingLibrary from "@testing-library/react";

import { installBrowserApis, type ObserverRegistry } from "./browser-apis";

export { installLayoutMetrics } from "./browser-apis";
export type { LayoutMetrics, ObserverRegistry } from "./browser-apis";
export { startApiServer } from "./api-server";
export type { ApiRequestRecord, ApiRouteHandler, ApiRoutes, ApiServer } from "./api-server";

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

  // React DOM reads the browser globals when it first loads, so import it only once the
  // environment above exists.
  // React DOM 在首次加载时读取浏览器全局对象，因此在上述环境建立之后再导入。
  const React = await import("react");
  // tsx compiles workspace .tsx sources with the classic JSX transform, which emits
  // React.createElement without adding an import.
  // tsx 以 classic JSX 转换编译工作区的 .tsx 源码，生成的 React.createElement 不会自带导入。
  Object.defineProperty(globalThis, "React", { configurable: true, value: React });

  const testingLibrary = await import("@testing-library/react");

  // A browser hands focus back to the body when the focused element leaves the DOM; jsdom hands
  // it to the document instead, and a later element.focus() then dispatches blur on the window.
  // Radix closes an open menu on that blur, so every render starts from the body as focus host.
  // 浏览器在获得焦点的元素离开 DOM 后把焦点交还给 body；jsdom 则交给 document，之后的
  // element.focus() 便向 window 派发 blur。Radix 收到该 blur 会关闭已打开的菜单，
  // 因此每次渲染都先让 body 充当焦点宿主。
  window.document.body.tabIndex = -1;
  const render = ((
    ui: Parameters<typeof testingLibrary.render>[0],
    options?: Parameters<typeof testingLibrary.render>[1],
  ) => {
    window.document.body.focus();
    return testingLibrary.render(ui, options);
  }) as typeof TestingLibrary.render;

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
    render,
    screen: testingLibrary.screen,
    waitFor: testingLibrary.waitFor,
    within: testingLibrary.within,
  };
}
