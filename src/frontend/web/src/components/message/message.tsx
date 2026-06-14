import React, { useMemo, useRef, useState } from "react";
import Image from "next/image";
import { AiMessage, AiMessageContent, MessageContentType } from "@/types";
import ReactMarkdown from "react-markdown";
import remarkGfm from "remark-gfm";
import { Button } from "../ui/button";
import { layoutWithLines, prepareWithSegments } from "@chenglou/pretext";
import { ChevronDown, ChevronUp } from "lucide-react";

type MessageNode = { type: string; content: string };

const isResultMessage = (message: AiMessage): boolean =>
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

const isTextNode = (type: string) =>
  (
    [
      MessageContentType.TextContent,
      MessageContentType.FunctionCallContent,
      MessageContentType.FunctionResultContent,

      MessageContentType.DataContent,
      MessageContentType.ErrorContent,
    ] as string[]
  ).includes(type);

const MdCard = ({ mdText: content }: { mdText: string }) => (
  <ReactMarkdown
    remarkPlugins={[remarkGfm]}
    components={{
      pre: ({ children }) => <pre className="msg-content-md-code">{children}</pre>,
      code: ({ children }) => <code className="msg-content-md-code">{children}</code>,
      ol: ({ children }) => <ol className="msg-content-md-ol">{children}</ol>,
      ul: ({ children }) => <ul className="msg-content-md-ul">{children}</ul>,
    }}
  >
    {content}
  </ReactMarkdown>
);

const Reasoning = ({ node }: { node: MessageNode }) => {
  const [expanded, setExpanded] = useState(false);
  const containerRef = useRef<HTMLDivElement>(null);

  const preview = useMemo(() => {
    console.debug("preview containerRef", containerRef);
    if (!containerRef || !containerRef.current) {
      return "...";
    }
    const el = containerRef.current;
    const rect = el.getBoundingClientRect();
    const style = getComputedStyle(el);

    const paddingLeft = parseFloat(style.paddingLeft);
    const paddingRight = parseFloat(style.paddingRight);

    const contentWidth = rect.width - paddingLeft - paddingRight;

    const maxWidth = contentWidth;

    const lines = node.content.split("\n");
    const firstLine = lines[0];
    const prepared = prepareWithSegments(firstLine, "16px Arial");
    console.debug("prepared", maxWidth, firstLine, JSON.stringify(prepared));
    const result = layoutWithLines(prepared, maxWidth, 22);

    //  const prepared = prepare(lines[0], "16px Inter");
    //  const { height, lineCount } = layout(prepared, maxWidth, 20);
    // console.log("prepared height, lineCount", height, lineCount);
    console.debug("prepared result", result.lines[0].text);
    return result.lines[0].text;
  }, [node.content, containerRef]);

  return (
    <div className="msg-content text-muted-foreground ">
      <div className="flex justify-between items-start">
        <div ref={containerRef} className="flex flex-1 flex-col">
          <MdCard mdText={expanded ? node.content : preview} />
        </div>
        <div>
          <Button
            variant="ghost"
            size="icon"
            className="w-[22] h-[22]"
            onClick={() => setExpanded(!expanded)}
          >
            {expanded ? <ChevronDown size={4} /> : <ChevronUp size={4} />}
          </Button>
        </div>
      </div>
    </div>
  );
};

const renderContent = (node: MessageNode, _: AiMessage): React.ReactNode => {
  if (isTextNode(node.type)) {
    return (
      <div className="msg-content">
        <MdCard mdText={node.content} />
      </div>
    );
  }

  if (node.type === MessageContentType.TextReasoningContent) {
    return <Reasoning node={node} />;
  }

  if (node.type === MessageContentType.UriContent) {
    return (
      <div className="msg-content">
        <Image src={node.content} alt="Image content" />
      </div>
    );
  }

  // if (node.type === MessageContentType.UsageContent) {
  //   return (
  //     <div className="msg-content w-full relative">
  //       <div className="w-full flex justify-center relative z-1">
  //         <Badge
  //           variant="secondary"
  //           className="bg-blue-500 text-white dark:bg-blue-600"
  //         >
  //           {node.content}
  //         </Badge>
  //       </div>
  //       <Separator className="w-full relative top-[-50%] z-0" />
  //     </div>
  //   );
  // }

  return null;
};

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

  console.debug("Rendering message", message.messageId, "with content nodes", groupContents);

  // const isUser = message.role === "user" && message.author === "user" && !message.additionalProperties;
  const isUser = message.role === "user";

  // TODO: There are pits here that need to be optimized
  const isToolUse = message.contents.some((c) => c.type === MessageContentType.FunctionCallContent);
  const isToolResult = message.contents.some(
    (c) => c.type === MessageContentType.FunctionResultContent,
  );

  const IsSideRight = isUser && !isToolResult;
  let title = "";
  if (isToolResult) {
    title = "Tool result";
  } else if (isToolUse) {
    title = "Tool use";
  } else if (isUser) {
    title = "You";
  } else if (isResult) {
    title = "Result";
  } else {
    title = `${message.role} (${message.author ?? "-"})`;
  }

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
    <div className={`flex ${IsSideRight ? "justify-end" : "justify-start"}`}>
      <div
        className={`min-w-0 max-w-full rounded-lg px-4 py-3 ${IsSideRight ? "bg-primary text-primary-foreground ml-12" : "bg-secondary mr-12"}`}
      >
        <div className={`flex items-center gap-2 mb-1 ${IsSideRight ? "justify-end" : ""}`}>
          <span className="text-xs font-semibold opacity-70">{title}</span>
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
