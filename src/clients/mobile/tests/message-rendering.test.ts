import { MessageContentType, type AiMessage } from "@agw/api";
import { scopeMessagesByUserTurn } from "@agw/execution-core";

import {
  getDisplayContentValue,
  getRenderableMessageContents,
  hasRenderableMessageContent,
} from "@/features/chat/message-rendering";

function message(contents: AiMessage["contents"]): AiMessage {
  return { messageId: "message-1", role: "assistant", contents };
}

test("message rendering filters blank and usage contents", () => {
  const current = message([
    { type: MessageContentType.TextContent, content: "" },
    { type: MessageContentType.TextReasoningContent, content: " \n" },
    { type: MessageContentType.UsageContent, content: { outputTokenCount: 1 } },
    { type: MessageContentType.TextContent, content: "visible" },
  ]);

  expect(getRenderableMessageContents(current)).toEqual([
    { type: MessageContentType.TextContent, content: "visible" },
  ]);
  expect(hasRenderableMessageContent(current)).toBe(true);
});

test("message rendering shows only the Claude Code hook event regardless of message role", () => {
  const current = message([
    {
      type: MessageContentType.TextContent,
      content: JSON.stringify({
        type: "system",
        hook_name: "SessionStart:startup",
        hook_event: "SessionStart",
      }),
    },
  ]);

  expect(getDisplayContentValue(current, current.contents[0])).toBe("SessionStart");
});

test("historical hook contents stay readable after adjacent text contents are merged", () => {
  const hookContent = JSON.stringify({
    type: "system",
    hook_name: "SessionStart:startup",
    hook_event: "SessionStart",
  });
  const [historical] = scopeMessagesByUserTurn([
    {
      messageId: "system-1",
      role: "system",
      contents: [
        { type: MessageContentType.TextContent, content: hookContent },
        { type: MessageContentType.TextContent, content: hookContent },
      ],
    },
  ]);

  expect(historical.contents).toHaveLength(1);
  expect(getDisplayContentValue(historical, historical.contents[0])).toBe("SessionStart");
});

test("message rendering keeps images and visible errors", () => {
  const current = message([
    { type: MessageContentType.DataContent, uri: "data:image/png;base64,AA==" },
    { type: MessageContentType.ErrorContent, content: "" },
    { type: MessageContentType.DataContent, uri: "" },
  ]);

  expect(getRenderableMessageContents(current)).toEqual([
    { type: MessageContentType.DataContent, uri: "data:image/png;base64,AA==" },
    { type: MessageContentType.ErrorContent, content: "" },
  ]);
});
