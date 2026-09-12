import { render } from "@testing-library/react-native";
import React from "react";

import { NativeConversationHistory } from "@agw/chat-native/conversation";
import { getExecutionReconnectProgress } from "@agw/chat-native/execution";

jest.mock("lucide-react-native", () => new Proxy({}, { get: () => () => null }));

jest.mock("expo-image", () => ({ Image: () => null }));
jest.mock("react-native-enriched-markdown", () => ({ EnrichedMarkdownText: () => null }));

test("Mobile keeps the first five retries silent and shows only the visible five", async () => {
  const view = await render(<NativeConversationHistory items={[]} />);
  for (let retryAttempt = 1; retryAttempt <= 10; retryAttempt += 1) {
    const reconnectState = { status: "reconnecting" as const, retryAttempt, retryDelayMs: 1_000 };
    await view.rerender(<NativeConversationHistory items={[]} reconnectState={reconnectState} />);
    if (retryAttempt <= 5) {
      expect(view.queryByText(/Reconnecting/)).toBeNull();
      expect(getExecutionReconnectProgress(reconnectState)).toBeNull();
    } else {
      expect(view.getByText(`Reconnecting to the execution… ${retryAttempt - 5}/5`)).toBeTruthy();
    }
  }
  await view.rerender(<NativeConversationHistory items={[]} reconnectState={null} />);
  expect(view.queryByText(/Reconnecting/)).toBeNull();
  await view.rerender(
    <NativeConversationHistory
      items={[]}
      reconnectState={{ status: "failed", retryAttempt: 10, retryDelayMs: 0 }}
    />,
  );
  expect(view.getByText("Execution connection failed. 5/5")).toBeTruthy();
});
