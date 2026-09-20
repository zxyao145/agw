import { render } from "@testing-library/react-native";
import React from "react";
import type { AiMessage } from "@agw/api";
import { NativeConversationHistoryHost } from "@agw/chat-native/conversation";

jest.mock("lucide-react-native", () => new Proxy({}, { get: () => () => null }));
jest.mock("expo-image", () => ({ Image: () => null }));
jest.mock("react-native-enriched-markdown", () => ({ EnrichedMarkdownText: () => null }));

test.each(["system-agent", "claude-code", "codex", "pi"])(
  "Mobile formats %s Results only after its response schema is available",
  async (author) => {
    const messages: AiMessage[] = [
      {
        messageId: "result",
        role: "assistant",
        author,
        additionalProperties: { type: "result" },
        contents: [{ type: "TextContent", content: '{"approved":false}' }],
      },
    ];
    const formatted = '{\n  "approved": false\n}';
    const view = await render(
      <NativeConversationHistoryHost messages={messages} activeAgentId="agent" />,
    );
    expect(view.queryByText(formatted)).toBeNull();

    await view.rerender(
      <NativeConversationHistoryHost
        messages={messages}
        activeAgentId="agent"
        agentResultFormats={[{ id: "agent", resultFormat: "json" }]}
      />,
    );
    expect(view.getByText(formatted)).toBeTruthy();

    await view.rerender(
      <NativeConversationHistoryHost
        messages={messages}
        activeAgentId="agent"
        agentResultFormats={[{ id: "agent", resultFormat: "markdown" }]}
      />,
    );
    expect(view.queryByText(formatted)).toBeNull();
  },
);
