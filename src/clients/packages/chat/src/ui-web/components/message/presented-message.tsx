import * as React from "react";

import { MessageContentType } from "@agw/api";
import { cn } from "@agw/components";
import {
  createAutoScrollState,
  updateAutoScrollState,
  type PresentedContent,
  type PresentedMessage,
} from "@agw/chat-core";
import { isResultMessage } from "@agw/execution-core";

import { MessageCitations, ToolState, isToolStateMessage } from "./tool-state";
import DataContent from "./renders/data-content";
import PlanCard from "./renders/plan-card";
import Reasoning from "./renders/reasoning";
import TextContent from "./renders/text-content";
import UriContent from "./renders/uri-content";
import { ToolDirectoryInfo } from "./tool-directory";
import { MessageActions } from "./message-actions";

function PresentedContentView({ content }: { content: PresentedContent }) {
  if (content.type === "json") {
    return (
      <pre className="max-w-full whitespace-pre-wrap break-words font-mono text-sm leading-relaxed">
        {content.text}
      </pre>
    );
  }
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

/**
 * 消息内容超过 max-h-80 时在内部滚动。followOutput 为 true 时，内容增长后保持在底部；
 * 用户向上滚动后停止跟随，滚回底部后恢复。
 * Scrolls message content that exceeds max-h-80. With followOutput, growing content stays at
 * the bottom; scrolling up stops following and scrolling back to the bottom resumes it.
 */
function MessageScrollArea({
  followOutput,
  children,
}: {
  followOutput: boolean;
  children: React.ReactNode;
}) {
  const scrollRef = React.useRef<HTMLDivElement>(null);
  const contentRef = React.useRef<HTMLDivElement>(null);
  const autoScrollStateRef = React.useRef(createAutoScrollState());

  React.useEffect(() => {
    if (!followOutput) return;
    const scrollElement = scrollRef.current!;
    const resizeObserver = new ResizeObserver(() => {
      if (autoScrollStateRef.current.shouldAutoScroll) {
        scrollElement.scrollTop = scrollElement.scrollHeight;
      }
      autoScrollStateRef.current = {
        ...autoScrollStateRef.current,
        scrollHeight: scrollElement.scrollHeight,
        scrollTop: scrollElement.scrollTop,
      };
    });
    resizeObserver.observe(contentRef.current!);
    return () => resizeObserver.disconnect();
  }, [followOutput]);

  return (
    <div
      ref={scrollRef}
      className="max-h-80 overflow-y-auto agw-scrollbar"
      onScroll={(event) => {
        autoScrollStateRef.current = updateAutoScrollState(
          autoScrollStateRef.current,
          event.currentTarget,
        );
      }}
    >
      <div ref={contentRef}>{children}</div>
    </div>
  );
}

function PresentedMessageView({
  message,
  embedded = false,
  streaming = false,
}: {
  message: PresentedMessage;
  embedded?: boolean;
  /** 消息所在的 Turn 正在流式输出。The turn this message belongs to is streaming. */
  streaming?: boolean;
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
  const messageContent = (
    <>
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
      {message.source.contents.map((content, index) => (
        <ToolDirectoryInfo key={`directory:${index}`} content={content} />
      ))}
      <div className={cn("agw-msg-body", isUser ? "items-end" : "")}>
        {message.contents.map((content, index) => (
          <PresentedContentView key={`${content.type}:${index}`} content={content} />
        ))}
      </div>
      <MessageCitations message={message.source} />
    </>
  );

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
          isUser || isResult ? "group/message relative" : "",
          isUser ? "agw-msg-user" : "msg-pos-left w-full",
          isResult ? "agw-msg-result" : "",
          message.width === "full" ? "w-full" : "",
        )}
      >
        {isResult ? (
          messageContent
        ) : (
          <MessageScrollArea followOutput={streaming && !isUser}>
            {messageContent}
          </MessageScrollArea>
        )}
        {isUser || isResult ? <MessageActions message={message} /> : null}
      </div>
    </div>
  );
}

export const PresentedMessageComponent = React.memo(PresentedMessageView);
