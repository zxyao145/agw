import { fireEvent, render } from "@testing-library/react-native";
import React from "react";
import type { AiMessage } from "@agw/api";
import { NativeConversationHistoryHost } from "@agw/chat-native/conversation";

jest.mock("lucide-react-native", () => new Proxy({}, { get: () => () => null }));
jest.mock("expo-image", () => ({ Image: () => null }));
jest.mock("react-native-enriched-markdown", () => ({
  EnrichedMarkdownText: ({ markdown }: { markdown: string }) => {
    const { Text } = require("react-native");
    return <Text>{markdown}</Text>;
  },
}));
const messages: AiMessage[] = [
  {
    messageId: "user",
    role: "user",
    createdAt: "2026-09-20T01:00:00Z",
    contents: [{ type: "TextContent", content: "Review the change" }],
  },
  {
    messageId: "process",
    role: "assistant",
    contents: [{ type: "TextContent", content: "Checking implementation" }],
  },
  {
    messageId: "result",
    role: "assistant",
    createdAt: "2026-09-20T01:17:39Z",
    additionalProperties: { type: "result" },
    contents: [{ type: "TextContent", content: "Review complete" }],
  },
];

test("Mobile folds completed work, exposes an accessible toggle, and resets on conversation change", async () => {
  const view = await render(
    <NativeConversationHistoryHost messages={messages} conversationKey="a" />,
  );
  const trigger = view.getByRole("button", { name: "Worked for 17m 39s" });
  expect(trigger.props.accessibilityState.expanded).toBe(false);
  expect(view.getByText("Review the change")).toBeTruthy();
  expect(view.getByText("Review complete")).toBeTruthy();
  expect(view.queryByText("Checking implementation")).toBeNull();
  await fireEvent.press(trigger);
  expect(view.getByText("Checking implementation")).toBeTruthy();
  expect(
    view.getByRole("button", { name: "Worked for 17m 39s" }).props.accessibilityState.expanded,
  ).toBe(true);
  await view.rerender(
    <NativeConversationHistoryHost messages={[...messages]} conversationKey="a" />,
  );
  expect(view.getByText("Checking implementation")).toBeTruthy();
  await fireEvent.press(view.getByRole("button", { name: "Worked for 17m 39s" }));
  expect(view.queryByText("Checking implementation")).toBeNull();
  await fireEvent.press(view.getByRole("button", { name: "Worked for 17m 39s" }));
  await view.rerender(<NativeConversationHistoryHost messages={messages} conversationKey="b" />);
  expect(view.queryByText("Checking implementation")).toBeNull();
});

test("Mobile keeps work visible through execution and silent reconnect, then folds on completion", async () => {
  const view = await render(
    <NativeConversationHistoryHost messages={messages} isCurrentTurnActive />,
  );
  expect(view.queryByRole("button", { name: "Worked for 17m 39s" })).toBeNull();
  expect(view.getByText("Checking implementation")).toBeTruthy();
  await view.rerender(
    <NativeConversationHistoryHost
      messages={messages}
      reconnectState={{ status: "reconnecting", retryAttempt: 1, retryDelayMs: 1000 }}
    />,
  );
  expect(view.queryByRole("button", { name: "Worked for 17m 39s" })).toBeNull();
  await view.rerender(<NativeConversationHistoryHost messages={messages} />);
  expect(view.getByRole("button", { name: "Worked for 17m 39s" })).toBeTruthy();
  expect(view.queryByText("Checking implementation")).toBeNull();
});
