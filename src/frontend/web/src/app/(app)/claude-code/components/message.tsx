import React from "react";
import Image from "next/image";
import {
  AiMessage,
  AiMessageContent,
  MessageContentType,
  MessageContentTypes,
} from "../types";
import { Badge } from "@/components/ui/badge";
import { Separator } from "@/components/ui/separator";
import ReactMarkdown from "react-markdown";
import remarkGfm from "remark-gfm";

export const AiMessageComponment = ({ message }: { message: AiMessage }) => {
  const isUser = message.role === "user"
    &&  message.author === "user"
    && !message.additionalProperties;
  const isResult = message.role === "system"
    && message.additionalProperties!.type === "result";

  const errStyle = "bg-destructive/10 border border-destructive/20 mr-12";

  const pasrseContents = (massage : AiMessage) => {
    const { contents } = massage
    const nodes: React.ReactNode[] = [];
    let curNode: unknown;
    let lastType: string = "";

    const appendNodes = () => {
      let node: React.ReactNode = null;
      switch (lastType) {
        case MessageContentType.DataContent:
        case MessageContentType.ErrorContent:
        case MessageContentType.FunctionCallContent:
        case MessageContentType.FunctionResultContent:
        case MessageContentType.TextContent:
        case MessageContentType.TextReasoningContent:
          node = (
            <div className="text-sm whitespace-pre-wrap wrap-break-word">
              <ReactMarkdown remarkPlugins={[remarkGfm]}>
                {curNode as string}
              </ReactMarkdown>
            </div>
          );
          break;
        case MessageContentType.UriContent:
          node = (
            <div className="text-sm whitespace-pre-wrap wrap-break-word">
              <Image src={curNode as string} alt="Picture of the author" />
            </div>
          );
          break;
        case MessageContentType.UsageContent:
          node = (
            <div className="text-sm whitespace-pre-wrap wrap-break-word w-full relative">
              <div className="w-full flex justify-center relative z-1">
                <Badge
                  variant="secondary"
                  className="bg-blue-500 text-white dark:bg-blue-600"
                >
                  {curNode as string}
                </Badge>
              </div>
              <Separator className="w-full relative top-[-50%] z-0" />
            </div>
          );
          break;
      }
      if (node) {
        nodes.push(node);
      }
    };

    const initNode = (type: string) => {
      switch (type) {
        case MessageContentType.DataContent:
          curNode = "";
          break;
        case MessageContentType.ErrorContent:
          curNode = "ERROR: ";
          break;
        case MessageContentType.FunctionCallContent:
          curNode = "Tool use: ";
          break;
        case MessageContentType.FunctionResultContent:
          curNode = "Tool result: ";
          break;
        case MessageContentType.TextContent:
          curNode = "";
          break;
        case MessageContentType.TextReasoningContent:
          curNode = "Thinking:";
          break;
        case MessageContentType.UriContent:
          curNode = "";
          break;
        case MessageContentType.UsageContent:
          curNode = "Usage:";
          break;
      }
    };

    const buildCurNode = ({ type, content }: AiMessageContent) => {
      switch (type) {
        case MessageContentType.DataContent:
        case MessageContentType.ErrorContent:
        case MessageContentType.FunctionCallContent:
        case MessageContentType.FunctionResultContent:
        case MessageContentType.TextContent:
        case MessageContentType.TextReasoningContent:
          if(content.startsWith("<local-command-stdout>")){
            curNode += content.replace("<local-command-stdout>", "")
            .replace("</local-command-stdout>", "");
          }else{
            curNode += content;
          }
          break;
        case MessageContentType.UriContent:
          curNode = content;
          break;
        case MessageContentType.UsageContent:
          const contentAny = content as any;
          curNode = `inputToken: ${contentAny.inputTokenCount} • outputToken: ${contentAny.outputTokenCount}`;
          const usd = message?.additionalProperties?.totalCostUsd;
          if(usd !== undefined){
            curNode += ` • totalCost: ${usd} (USD)`;
          }
          break;
      }
    };

    for (const content of contents) {
      const type = content.type;
      if (lastType === "") {
        initNode(type);
      } else if (type !== lastType) {
        appendNodes();
        initNode(type);
      }
      buildCurNode(content);
      lastType = type;
    }
    appendNodes();
    return nodes;
  };


  if (isResult) {
    return (
      <div className={`flex justify-center`}>
         {...pasrseContents(message)} 
      </div>
    );
  }
 
  return (
    <div className={`flex ${isUser ? "justify-end" : "justify-start"}`}>
      <div
        className={`max-w-[80%] rounded-lg px-4 py-3 ${
          isUser
            ? "bg-primary text-primary-foreground ml-12"
            :  "bg-secondary mr-12"
        }`}
      >
        <div className="flex items-center gap-2 mb-1">
          <span className="text-xs font-semibold opacity-70">
            {isUser ? "You" : message.role || message.author }
          </span>
        </div>
        <div className="text-sm whitespace-pre-wrap wrap-break-word">
          {...pasrseContents(message)}
        </div>
      </div>
    </div>
  );
};
