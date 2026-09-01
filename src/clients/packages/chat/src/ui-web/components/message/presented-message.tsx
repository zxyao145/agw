import * as React from "react";

import { MessageContentType } from "@agw/api";
import { cn } from "@agw/components";
import type { PresentedContent, PresentedMessage } from "@agw/chat-core";
import { isResultMessage } from "@agw/execution-core";

import { MessageCitations, ToolState, isToolStateMessage } from "./tool-state";
import DataContent from "./renders/data-content";
import PlanCard from "./renders/plan-card";
import Reasoning from "./renders/reasoning";
import TextContent from "./renders/text-content";
import UriContent from "./renders/uri-content";

function PresentedContentView({ content }: { content: PresentedContent }) {
  if (content.type === "plan") return <PlanCard {...content} />;
  if (content.type === "image") {
    return (
      <DataContent
        node={{
          type: MessageContentType.DataContent,
          content: content.uri,
          name: content.name ?? undefined,
        }}
      />
    );
  }
  if (content.type === "uri") {
    return (
      <UriContent
        node={{
          type: MessageContentType.UriContent,
          content: content.uri,
          name: content.name ?? undefined,
        }}
      />
    );
  }
  if (content.type === "reasoning") {
    return (
      <Reasoning
        node={{ type: MessageContentType.TextReasoningContent, content: content.markdown }}
      />
    );
  }
  if (content.type === "error") {
    return (
      <p className="whitespace-pre-wrap break-words text-sm leading-relaxed text-destructive">
        {content.text}
      </p>
    );
  }
  if (content.type === "plain") {
    return (
      <p className="whitespace-pre-wrap break-words text-sm leading-relaxed">{content.text}</p>
    );
  }
  return <TextContent node={{ type: content.sourceType, content: content.markdown }} />;
}

function PresentedMessageView({
  message,
  embedded = false,
}: {
  message: PresentedMessage;
  embedded?: boolean;
}) {
  if (isToolStateMessage(message.source)) return <ToolState message={message.source} />;

  const isResult = isResultMessage(message.source);
  const isUser = message.alignment === "right";
  const isToolUse = message.source.contents.some(
    (content) => content.type === MessageContentType.FunctionCallContent,
  );
  const isToolResult = message.source.contents.some(
    (content) => content.type === MessageContentType.FunctionResultContent,
  );
  const title = isResult ? "Result" : isToolResult ? "Tool result" : isToolUse ? "Tool use" : "";

  return (
    <div
      className={cn(
        "flex",
        isUser ? "justify-end" : "justify-start",
        embedded || message.width === "full" || isUser ? "w-full" : "max-w-[80%]",
        isUser || isResult ? "mb-8" : "",
      )}
    >
      <div
        className={cn(
          "min-w-0 max-w-full",
          isUser
            ? "msg-pos-right rounded-lg bg-[#f3f3f4] px-2 py-1 text-[#17191d]"
            : "msg-pos-left w-full",
          isResult ? "w-full rounded-md border border-[#e4e4e5] bg-card p-3" : "",
          message.width === "full" ? "w-full" : "",
        )}
      >
        {title ? (
          <div className={cn("flex items-center gap-2", isUser && "justify-end")}>
            <span
              className={cn(
                "text-xs opacity-40",
                isResult && "mb-4 border-b border-dashed pr-4 text-lg font-semibold opacity-80",
              )}
            >
              {title}
            </span>
          </div>
        ) : null}
        <div className="msg-content-container">
          {message.contents.map((content, index) => (
            <PresentedContentView key={`${content.type}:${index}`} content={content} />
          ))}
        </div>
        <MessageCitations message={message.source} />
      </div>
    </div>
  );
}

export const PresentedMessageComponent = React.memo(PresentedMessageView);
