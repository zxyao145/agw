import { createUuidV7, MessageContentType, type AiMessage } from "@agw/api";
import { cloneMessageContent, type ExecutionUserInput } from "@agw/execution-core";

import type { ChatImageAttachment } from "./image-attachments";

const defaultUser = "$agw";

export function createUserTextMessage(input: string): AiMessage {
  return createUserMessage(input, []);
}

export function createUserMessage(
  input: string,
  imageAttachments: readonly ChatImageAttachment[],
): AiMessage {
  const contents: AiMessage["contents"] = imageAttachments.map((attachment) => ({
    type: MessageContentType.DataContent,
    uri: attachment.dataUrl,
    name: attachment.name,
  }));
  if (input.trim()) {
    contents.push({ type: MessageContentType.TextContent, content: input });
  }

  return {
    messageId: createUuidV7(),
    author: defaultUser,
    role: "user",
    contents,
  };
}

export function toExecutionUserInput(message: AiMessage): ExecutionUserInput<AiMessage> {
  return {
    messageId: message.messageId,
    author: message.author,
    contents: message.contents.map(cloneMessageContent),
  };
}
