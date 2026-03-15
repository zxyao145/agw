import React from "react";
import Image from "next/image";
import { AiMessage, AiMessageContent, MessageContentType } from "@/types";
import { Badge } from "@/components/ui/badge";
import { Separator } from "@/components/ui/separator";
import ReactMarkdown from "react-markdown";
import remarkGfm from "remark-gfm";

type MessageNode = { type: string; content: string };

const stripCommandTags = (str: string) =>
  str.replace("<local-command-stdout>", "").replace("</local-command-stdout>", "");

const getNodePrefix = (type: string): string =>
  type === MessageContentType.ErrorContent ? "ERROR: " :
  type === MessageContentType.FunctionCallContent ? "Tool use: " :
  type === MessageContentType.FunctionResultContent ? "Tool result: " :
  type === MessageContentType.TextReasoningContent ? "Thinking:" :
  type === MessageContentType.UsageContent ? "Usage:" :
  "";

const isTextNode = (type: string) => ([
  MessageContentType.DataContent,
  MessageContentType.ErrorContent,
  MessageContentType.FunctionCallContent,
  MessageContentType.FunctionResultContent,
  MessageContentType.TextContent,
  MessageContentType.TextReasoningContent,
] as string[]).includes(type);

const renderNode = (node: MessageNode, message: AiMessage): React.ReactNode => {
  if (isTextNode(node.type)) {
    return (
      <div className="text-sm whitespace-pre-wrap break-words [overflow-wrap:anywhere]">
        <ReactMarkdown
          remarkPlugins={[remarkGfm]}
          components={{
            pre: ({ children }) => (
              <pre className="max-w-full whitespace-pre-wrap break-words [overflow-wrap:anywhere]">{children}</pre>
            ),
            code: ({ children }) => (
              <code className="max-w-full whitespace-pre-wrap break-words [overflow-wrap:anywhere]">{children}</code>
            ),
          }}
        >
          {node.content}
        </ReactMarkdown>
      </div>
    );
  }

  if (node.type === MessageContentType.UriContent) {
    return (
      <div className="text-sm whitespace-pre-wrap break-words [overflow-wrap:anywhere]">
        <Image src={node.content} alt="Image content" />
      </div>
    );
  }

  if (node.type === MessageContentType.UsageContent) {
    return (
      <div className="text-sm whitespace-pre-wrap break-words [overflow-wrap:anywhere] w-full relative">
        <div className="w-full flex justify-center relative z-1">
          <Badge variant="secondary" className="bg-blue-500 text-white dark:bg-blue-600">
            {node.content}
          </Badge>
        </div>
        <Separator className="w-full relative top-[-50%] z-0" />
      </div>
    );
  }

  return null;
};

const buildContentNode = (content: AiMessageContent, message: AiMessage): string => {
  const { type, content: value } = content;

  if (type === MessageContentType.UsageContent) {
    const contentAny = value as unknown as { inputTokenCount: number; outputTokenCount: number };
    let result = `inputToken: ${contentAny.inputTokenCount} • outputToken: ${contentAny.outputTokenCount}`;
    const usd = message?.additionalProperties?.totalCostUsd as number | undefined;
    if (usd !== undefined) result += ` • totalCost: ${usd} (USD)`;
    return result;
  }

  if (type === MessageContentType.UriContent) {
    return value as string;
  }

  let processed = typeof value === "string" && value.startsWith("<local-command-stdout>")
    ? stripCommandTags(value)
    : (value as string);

  if (type === MessageContentType.FunctionCallContent || type === MessageContentType.FunctionResultContent) {
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

const groupContentsByType = (contents: AiMessageContent[], message: AiMessage): MessageNode[] => {
  const nodes: MessageNode[] = [];
  let currentContent = "";
  let lastType = "";

  for (const content of contents) {
    const { type } = content;

    if (lastType && type !== lastType) {
      nodes.push({ type: lastType, content: currentContent });
      currentContent = "";
    }

    currentContent += (currentContent ? "" : getNodePrefix(type)) + buildContentNode(content, message);
    lastType = type;
  }

  if (lastType) {
    nodes.push({ type: lastType, content: currentContent });
  }

  return nodes;
};

export const AiMessageComponent = ({ message }: { message: AiMessage }) => {
  // const isUser = message.role === "user" && message.author === "user" && !message.additionalProperties;
  const isUser = message.role === "user";
  const isResult = message.role === "system" && message.additionalProperties?.type === "result";
  const contentNodes = React.useMemo(() => groupContentsByType(message.contents, message), [message]);

  if (isResult) {
    return null;
    // return <div className="flex justify-center">{contentNodes.map((n, i) => <React.Fragment key={i}>{renderNode(n, message)}</React.Fragment>)}</div>;
  }

  console.debug("AiMessageComponent isUser", isUser, message)
  return (
    <div className={`flex ${isUser ? "justify-end" : "justify-start"}`}>
      <div className={`min-w-0 max-w-full rounded-lg px-4 py-3 ${isUser ? "bg-primary text-primary-foreground ml-12" : "bg-secondary mr-12"}`}>
        <div className={`flex items-center gap-2 mb-1 ${isUser ? "justify-end" : ""}`}>
          <span className="text-xs font-semibold opacity-70">
            {isUser ? "You" : `${message.role} (${message.author ?? "-"})`}
          </span>
        </div>
        <div className="text-sm whitespace-pre-wrap break-words [overflow-wrap:anywhere]">
          {contentNodes.map((n, i) => <React.Fragment key={i}>{renderNode(n, message)}</React.Fragment>)}
        </div>
      </div>
    </div>
  );
};
