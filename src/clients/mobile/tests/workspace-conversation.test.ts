/// <reference types="node" />

import { readFileSync } from "node:fs";
import { resolve } from "node:path";

const source = readFileSync(
  resolve(process.cwd(), "../packages/chat-native/src/native-workspace-provider.tsx"),
  "utf8",
);

test("Mobile clears records while preserving the active conversation id", () => {
  const clearHandlerStart = source.indexOf("const clearCurrentContext = React.useCallback");
  const clearHandlerEnd = source.indexOf(
    "const renameContext = React.useCallback",
    clearHandlerStart,
  );
  const clearHandler = source.slice(clearHandlerStart, clearHandlerEnd);

  expect(clearHandlerStart).toBeGreaterThan(-1);
  expect(clearHandlerEnd).toBeGreaterThan(-1);
  expect(clearHandler).toMatch(/clearProjectContextRecords\(selectedProjectId, contextToClear\)/);
  expect(clearHandler).toMatch(/selectedContextIdRef\.current = contextToClear/);
  expect(clearHandler).toMatch(/setSelectedContextId\(contextToClear\)/);
  expect(clearHandler).not.toMatch(/selectedContextIdRef\.current = null/);
  expect(clearHandler).not.toMatch(/setSelectedContextId\(null\)/);
});

test("Mobile sends the next message through the preserved conversation id", () => {
  const sendHandlerStart = source.indexOf("const sendMessage = React.useCallback");
  const sendHandlerEnd = source.indexOf(
    "const stopExecution = React.useCallback",
    sendHandlerStart,
  );
  const sendHandler = source.slice(sendHandlerStart, sendHandlerEnd);

  expect(sendHandlerStart).toBeGreaterThan(-1);
  expect(sendHandlerEnd).toBeGreaterThan(-1);
  expect(sendHandler).toMatch(/const contextId = ensureContextId\(\)/);
  expect(sendHandler).toMatch(/ensureConfiguredSession\(contextId, permissionMode\)/);
});
