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

test("Mobile shows the Agent name before the work duration", async () => {
  const history = [messages[0], messages[1], { ...messages[2], author: "claude-code" }];
  const view = await render(<NativeConversationHistoryHost messages={history} />);
  expect(view.getByRole("button", { name: "claude-code Worked for 17m 39s" })).toBeTruthy();
  expect(view.getByText("claude-code")).toBeTruthy();
});

test("Mobile preserves completed work through execution and reconnect", async () => {
  const view = await render(
    <NativeConversationHistoryHost messages={messages} isCurrentTurnActive />,
  );
  expect(view.getByRole("button", { name: "Worked for 17m 39s" })).toBeTruthy();
  expect(view.queryByText("Checking implementation")).toBeNull();
  await fireEvent.press(view.getByRole("button", { name: "Worked for 17m 39s" }));
  await view.rerender(
    <NativeConversationHistoryHost
      messages={messages}
      reconnectState={{ status: "reconnecting", retryAttempt: 1, retryDelayMs: 1000 }}
    />,
  );
  expect(
    view.getByRole("button", { name: "Worked for 17m 39s" }).props.accessibilityState.expanded,
  ).toBe(true);
  await view.rerender(<NativeConversationHistoryHost messages={messages} />);
  expect(view.getByRole("button", { name: "Worked for 17m 39s" })).toBeTruthy();
  expect(view.getByText("Checking implementation")).toBeTruthy();
});

test("Mobile gives commit and push independent work summaries while the flow continues", async () => {
  const history: AiMessage[] = [
    messages[0],
    messages[1],
    {
      ...messages[2],
      createdAt: "2026-09-20T01:00:20Z",
      additionalProperties: { type: "result", nodeName: "commit" },
    },
    {
      ...messages[1],
      messageId: "push-process",
      contents: [{ type: "TextContent", content: "Pushing changes" }],
    },
    {
      ...messages[2],
      messageId: "push-result",
      createdAt: "2026-09-20T01:00:46Z",
      additionalProperties: { type: "result", nodeName: "push" },
      contents: [{ type: "TextContent", content: "Push complete" }],
    },
  ];
  const view = await render(
    <NativeConversationHistoryHost messages={history.slice(0, 4)} isCurrentTurnActive />,
  );
  await fireEvent.press(view.getByRole("button", { name: "commit Worked for 20s" }));
  expect(view.getByText("Checking implementation")).toBeTruthy();
  expect(view.getByText("Pushing changes")).toBeTruthy();

  await view.rerender(<NativeConversationHistoryHost messages={history} isCurrentTurnActive />);
  expect(
    view.getByRole("button", { name: "commit Worked for 20s" }).props.accessibilityState.expanded,
  ).toBe(true);
  expect(
    view.getByRole("button", { name: "push Worked for 26s" }).props.accessibilityState.expanded,
  ).toBe(false);
  expect(view.queryByText("Pushing changes")).toBeNull();
  expect(view.getByText("Push complete")).toBeTruthy();
  await fireEvent.press(view.getByRole("button", { name: "push Worked for 26s" }));
  expect(view.getByText("Pushing changes")).toBeTruthy();
});
