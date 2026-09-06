/// <reference types="node" />

import { readFileSync } from "node:fs";
import { resolve } from "node:path";

const source = readFileSync(
  resolve(process.cwd(), "../packages/chat-native/src/native-workspace-provider.tsx"),
  "utf8",
);

test("Mobile clears records while preserving the active conversation id", () => {
  const clearHandlerStart = source.indexOf("const clearCurrentConversation = React.useCallback");
  const clearHandlerEnd = source.indexOf(
    "const renameConversation = React.useCallback",
    clearHandlerStart,
  );
  const clearHandler = source.slice(clearHandlerStart, clearHandlerEnd);

  expect(clearHandlerStart).toBeGreaterThan(-1);
  expect(clearHandlerEnd).toBeGreaterThan(-1);
  expect(clearHandler).toMatch(
    /clearProjectConversationRecords\([\s\S]*?selectedProjectId,[\s\S]*?conversationToClear/,
  );
  expect(clearHandler).toMatch(/selectedConversationIdRef\.current !== conversationToClear/);
  expect(clearHandler).toMatch(/selectedContextIdRef\.current = contextToClear/);
  expect(clearHandler).toMatch(/setSelectedContextId\(contextToClear\)/);
  expect(clearHandler).toMatch(/queryClient\.cancelQueries/);
  expect(clearHandler).toMatch(/queryClient\.removeQueries/);
  expect(clearHandler.indexOf("const cleared = await")).toBeLessThan(
    clearHandler.indexOf("setMessages([])"),
  );
  expect(clearHandler).not.toMatch(/selectedContextIdRef\.current = null/);
  expect(clearHandler).not.toMatch(/setSelectedContextId\(null\)/);
});

test("Mobile sends the next message through the preserved execution context id", () => {
  const sendHandlerStart = source.indexOf("const sendMessage = React.useCallback");
  const sendHandlerEnd = source.indexOf(
    "const stopExecution = React.useCallback",
    sendHandlerStart,
  );
  const sendHandler = source.slice(sendHandlerStart, sendHandlerEnd);

  expect(sendHandlerStart).toBeGreaterThan(-1);
  expect(sendHandlerEnd).toBeGreaterThan(-1);
  expect(sendHandler).toMatch(/const conversationId = ensureConversationId\(\)/);
  expect(sendHandler).toMatch(/const contextId = ensureContextId\(\)/);
  expect(sendHandler).toMatch(/ensureConfiguredSession\(contextId, permissionMode\)/);
  expect(sendHandler).toMatch(/conversationId,/);
  expect(sendHandler).not.toMatch(/conversations\.find/);
  expect(sendHandler).not.toMatch(/conversation\.contextId === contextId/);
});

test("Mobile resets a new chat locally without pre-creating a conversation", () => {
  const newChatStart = source.indexOf("const newChat = React.useCallback");
  const newChatEnd = source.indexOf("const selectProject = React.useCallback", newChatStart);
  const newChatHandler = source.slice(newChatStart, newChatEnd);

  expect(newChatStart).toBeGreaterThan(-1);
  expect(newChatEnd).toBeGreaterThan(-1);
  expect(newChatHandler).not.toMatch(/createProjectConversation/);
  expect(newChatHandler).toMatch(/selectedConversationIdRef\.current = null/);
  expect(newChatHandler).toMatch(/selectedContextIdRef\.current = null/);
  expect(newChatHandler).toMatch(/setSelectedConversationId\(null\)/);
  expect(newChatHandler).toMatch(/setSelectedContextId\(null\)/);
});
