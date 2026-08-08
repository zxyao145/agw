import { MessageContentType, type AiMessage } from "@agw/api";

export type HumanInteractionCallTarget = {
  callId?: string;
  streamingScopeId?: string;
};

export function matchesHumanInteractionCall(
  message: AiMessage,
  target: HumanInteractionCallTarget,
): boolean {
  if (!target.callId) return false;
  if (target.streamingScopeId && message.streamingScopeId !== target.streamingScopeId) return false;

  return message.contents.some(
    (content) =>
      content.type === MessageContentType.FunctionCallContent &&
      content.additionalProperties?.callId === target.callId,
  );
}

export function hasMatchingHumanInteractionCall(
  messages: AiMessage[],
  target: HumanInteractionCallTarget,
): boolean {
  return messages.some((message) => matchesHumanInteractionCall(message, target));
}
