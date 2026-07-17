import React from "react";
import { AiMessage, AiMessageContent, MessageContentType } from "@agw/api";
import { cn } from "@agw/components";
import { MessageNode } from "./types";
import { renderContent } from "./renders";

export const isResultMessage = (message: AiMessage): boolean =>
  message.additionalProperties?.type === "result";

const stripCommandTags = (str: string) =>
  str.replace("<local-command-stdout>", "").replace("</local-command-stdout>", "");

const getNodePrefix = (type: string): string =>
  type === MessageContentType.ErrorContent
    ? "ERROR: "
    : type === MessageContentType.UsageContent
      ? "Usage: "
      : type === MessageContentType.FunctionResultContent
        ? ""
        : type === MessageContentType.TextReasoningContent
          ? ""
          : type === MessageContentType.FunctionCallContent
            ? ""
            : "";

const buildContentNode = (content: AiMessageContent, message: AiMessage): string => {
  const { type, content: value } = content;

  if (type === MessageContentType.UsageContent) {
    const contentAny = value as unknown as {
      inputTokenCount: number;
      outputTokenCount: number;
    };
    let result = `inputToken: ${contentAny.inputTokenCount} • outputToken: ${contentAny.outputTokenCount}`;
    const usd = message?.additionalProperties?.totalCostUsd as number | undefined;
    if (usd !== undefined) result += ` • totalCost: ${usd} (USD)`;
    return result;
  }

  if (type === MessageContentType.UriContent) {
    return value as string;
  }

  let processed =
    typeof value === "string" && value.startsWith("<local-command-stdout>")
      ? stripCommandTags(value)
      : (value as string);

  if (
    type === MessageContentType.FunctionCallContent ||
    type === MessageContentType.FunctionResultContent
  ) {
    if (processed.startsWith("{") && processed.endsWith("}")) {
      try {
        processed = "\n```json\n" + JSON.stringify(JSON.parse(processed), null, 2) + "\n```";
      } catch {
        // Keep original if invalid JSON
      }
    }
  }

  return processed;
};

/**
 * 流式响应的时候处理数据，按照内容类型进行分组，生成一个新的数组
 * @param
 * @returns
 */
const groupContentsByType = (message: AiMessage): MessageNode[] => {
  const contents = message.contents || [];
  const nodes: MessageNode[] = [];
  let currentContent = "";
  let lastType = "";

  for (const content of contents) {
    const { type } = content;

    if (lastType && type !== lastType) {
      nodes.push({ type: lastType, content: currentContent });
      currentContent = "";
    }

    currentContent +=
      (currentContent ? "" : getNodePrefix(type)) + buildContentNode(content, message);
    lastType = type;
  }

  if (lastType) {
    nodes.push({ type: lastType, content: currentContent });
  }

  return nodes;
};

export const AiMessageComponent = ({ message }: { message: AiMessage }) => {
  const isResult = isResultMessage(message);

  // group contents by type
  // To solve the problem of multiple returns of the same type in streaming mode
  // A more appropriate approach is to group by messageId. Why wasn't that done? already forgotten
  const groupContents = React.useMemo(() => groupContentsByType(message), [message]);

  // const isUser = message.role === "user" && message.author === "user" && !message.additionalProperties;
  const isUser = message.role === "user";
  const isUserBlock = message.role === "user" && !message.additionalProperties;

  // TODO: There are pits here that need to be optimized
  const isToolUse = message.contents.some((c) => c.type === MessageContentType.FunctionCallContent);
  const isToolResult = message.contents.some(
    (c) => c.type === MessageContentType.FunctionResultContent,
  );

  const IsSideRight = isUser && !isToolResult;
  let title = "";
  if (isResult) {
    title = "Result";
  } else if (isToolResult) {
    title = "Tool result";
  } else if (isToolUse) {
    title = "Tool use";
  }
  // else if (isUser) {
  //   title = "You";
  // } else {
  //   title = `${message.role} (${message.author ?? "-"})`;
  // }

  console.debug(
    "AiMessageComponent isUser",
    isUser,
    "isToolResult",
    isToolResult,
    "IsSideRight",
    IsSideRight,
    "isResult",
    isResult,
    "message",
    JSON.stringify(message),
  );
  return (
    <div
      className={cn(
        "flex",
        IsSideRight ? "justify-end" : "justify-start",
        isUserBlock || isResult ? "w-full mb-8" : "max-w-[80%]",
      )}
    >
      <div
        className={cn(
          "min-w-0 ",
          IsSideRight ? "msg-pos-right" : "msg-pos-left",
          isResult ? "w-full rounded-md p-3 border border-[#e4e4e5] bg-[##fffeff] " : "max-w-full",
          IsSideRight
            ? "rounded-lg px-2 py-1 bg-[#f3f3f4] text-[#17191d] ml-12"
            : // ? "rounded-lg px-2 py-1 bg-[#f3f3f4] ml-24"
              // debug
              // : "bg-secondary mr-12"}`}
              "mr-12",
        )}
      >
        <div className={`flex items-center gap-2 ${IsSideRight ? "justify-end" : ""}`}>
          <span
            className={`${isResult ? "text-lg opacity-80 font-semibold mb-4 border-b border-dashed pr-4" : "text-xs opacity-40"}`}
          >
            {title}
          </span>
        </div>

        <div className="msg-content-container">
          {groupContents.map((n, i) => (
            <React.Fragment key={i}>{renderContent(n, message)}</React.Fragment>
          ))}
        </div>
      </div>
    </div>
  );
};
