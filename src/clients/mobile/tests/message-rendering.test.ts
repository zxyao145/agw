import { MessageContentType, type AiMessage } from "@agw/api";

import {
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
