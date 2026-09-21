import type { ExecutionMessage, ExecutionMessageContent } from "./types";

export function isNormalizedMessage(message: ExecutionMessage): boolean {
  const operation = message.additionalProperties?.messageOperation;
  return (
    operation === "AppendText" ||
    operation === "PutBlock" ||
    operation === "PutMessage" ||
    operation === "SealMessage"
  );
}

function copyContent(content: ExecutionMessageContent): ExecutionMessageContent {
  return {
    ...content,
    additionalProperties: content.additionalProperties
      ? { ...content.additionalProperties }
      : undefined,
  };
}

/** Applies explicit operations; the returned value is always a complete local snapshot. */
export function applyMessageOperation<T extends ExecutionMessage>(
  existing: T | undefined,
  incoming: T,
): T {
  const operation = incoming.additionalProperties?.messageOperation;
  const replaces =
    operation === "PutMessage" || (operation === "SealMessage" && incoming.contents.length > 0);
  let contents = replaces
    ? incoming.contents.map(copyContent)
    : (existing?.contents ?? []).map(copyContent);
  if (operation === "AppendText" || operation === "PutBlock") {
    for (const content of incoming.contents) {
      const blockId = content.additionalProperties?.blockId;
      if (typeof blockId !== "string" || blockId.length === 0)
        throw new Error("Missing content block identity");
      const index = contents.findIndex((block) => block.additionalProperties?.blockId === blockId);
      if (operation === "AppendText" && index >= 0) {
        const previous = contents[index];
        if (
          previous.type !== content.type ||
          typeof previous.content !== "string" ||
          typeof content.content !== "string"
        ) {
          throw new Error("Invalid text append");
        }
        contents[index] = { ...copyContent(content), content: previous.content + content.content };
      } else if (index >= 0) contents[index] = copyContent(content);
      else contents.push(copyContent(content));
    }
  } else if (operation !== "PutMessage" && operation !== "SealMessage") {
    throw new Error("Unsupported message operation");
  }
  const properties = {
    ...existing?.additionalProperties,
    ...incoming.additionalProperties,
    messageOperation: "PutMessage",
  };
  return {
    ...existing,
    ...incoming,
    contents,
    createdAt: existing?.createdAt ?? incoming.createdAt,
    additionalProperties: properties,
  };
}
