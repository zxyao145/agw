/// <reference types="node" />

import { readFileSync } from "node:fs";
import { resolve } from "node:path";

const source = readFileSync(
  resolve(process.cwd(), "../packages/chat-native/src/native-workspace-provider.tsx"),
  "utf8",
);

test("Mobile changes Agent mode through the configured execution session", () => {
  const modeHandlerStart = source.indexOf("const setAgentMode = React.useCallback");
  const sendHandlerStart = source.indexOf("const sendMessage = React.useCallback");
  const modeHandler = source.slice(modeHandlerStart, sendHandlerStart);

  expect(modeHandlerStart).toBeGreaterThan(-1);
  expect(modeHandler).toMatch(/selectedTarget\.type !== "agent"/);
  expect(modeHandler).toMatch(/setAgentModeState\(nextAgentMode\)/);
  expect(modeHandler).toMatch(/configured\.session\.setMode\(selectedTarget\.id, nextAgentMode\)/);
  expect(source).toMatch(/onClose: \(\) => \{[\s\S]*?disposeExecutionSession\(\)/);
});

test("Mobile execution no longer sends Agent mode with every user message", () => {
  const sendHandlerStart = source.indexOf("const sendMessage = React.useCallback");
  const stopHandlerStart = source.indexOf("const stopExecution = React.useCallback");
  const sendHandler = source.slice(sendHandlerStart, stopHandlerStart);

  expect(sendHandlerStart).toBeGreaterThan(-1);
  expect(sendHandler).toMatch(/configured\.session\.execute\(/);
  expect(sendHandler).not.toMatch(/setMode\(/);
  expect(sendHandler).not.toMatch(/agentMode/);
});

test("Mobile consumes direct mode controls and restores persisted mode history", () => {
  const messageHandlerStart = source.indexOf("const applyExecutionMessage = React.useCallback");
  const disposeHandlerStart = source.indexOf("const disposeExecutionSession");
  const messageHandler = source.slice(messageHandlerStart, disposeHandlerStart);

  expect(messageHandler).toMatch(/setAgentModeState\(confirmedAgentModeRef\.current\)/);
  expect(messageHandler).toMatch(/const nextAgentMode = getAgentMode\(incoming\)/);
  expect(messageHandler).toMatch(/if \(isModeControlMessage\(incoming\)\) return/);
  expect(source).toMatch(/getLatestAgentMode\(contextDetailsQuery\.data\.messages\)/);
  expect(source).toMatch(/prepareClaudeHistory\(contextDetailsQuery\.data\.messages\)/);
  expect(source).toMatch(/scopeMessagesByUserTurn\(claudeHistory\.messages\)/);
});
