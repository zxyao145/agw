import assert from "node:assert/strict";
import test, { after, before } from "node:test";
import * as React from "react";
import { createElement } from "react";
import { renderToStaticMarkup } from "react-dom/server";
import { PathnameContext } from "next/dist/shared/lib/hooks-client-context.shared-runtime";

import { DesktopConnectionGate } from "./desktop-connection-gate";
import { DesktopRuntimeProvider } from "./runtime-provider";

const reactGlobal = Object.getOwnPropertyDescriptor(globalThis, "React");
before(() => Object.defineProperty(globalThis, "React", { configurable: true, value: React }));
after(() => {
  if (reactGlobal) Object.defineProperty(globalThis, "React", reactGlobal);
  else Reflect.deleteProperty(globalThis, "React");
});

function render(pathname: string) {
  return renderToStaticMarkup(
    createElement(PathnameContext.Provider, {
      value: pathname,
      children: createElement(DesktopRuntimeProvider, {
        children: createElement(DesktopConnectionGate, { children: "Workspace" }),
      }),
    }),
  );
}

test("DesktopConnectionGate_InitiallyLoading_ShowsConnectionStatus", () => {
  const markup = render("/desktop/chat/");
  assert.ok(markup.includes('role="status"'));
  assert.ok(markup.includes("Connecting to Agw Server…"));
  assert.ok(!markup.includes("Agw Desktop needs attention"));
  assert.ok(!markup.includes("Workspace"));
});

test("DesktopConnectionGate_LoadingLocalSettings_AllowsAccess", () => {
  assert.ok(render("/settings/").includes("Workspace"));
  assert.ok(render("/extensions/").includes("Workspace"));
});
