import { MessageContentType, type AiMessage, type AiMessageContent } from "@agw/api";

export function stringifyContentValue(value: unknown): string {
  if (typeof value === "string") return value;
  if (value == null) return "";
  return JSON.stringify(value, null, 2) ?? "";
}

export function isRenderableContent(content: AiMessageContent): boolean {
  if (content.type === MessageContentType.UsageContent) return false;
  if (content.type === MessageContentType.DataContent) {
    return content.uri?.startsWith("data:image/") === true;
  }
  if (content.type === MessageContentType.UriContent) {
    return Boolean(content.uri?.trim());
  }
  if (content.type === MessageContentType.ErrorContent) return true;

  return stringifyContentValue(content.content).trim().length > 0;
}

export function getRenderableMessageContents(message: AiMessage): AiMessageContent[] {
  return message.contents.filter(isRenderableContent);
}

export function hasRenderableMessageContent(message: AiMessage): boolean {
  return message.contents.some(isRenderableContent);
}
