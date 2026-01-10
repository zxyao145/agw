import React from "react";
import Image from "next/image";
import {
  AiMessage,
  AiMessageContent,
  MessageContentType,
  MessageContentTypes,
} from "./types";

export const AiMessageComponment = ({ message }: { message: AiMessage }) => {
  const isUser = message.role === "user";
  const errStyle = "bg-destructive/10 border border-destructive/20 mr-12";

  const pasrseContents = (contents: AiMessageContent[]) => {
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
              {curNode as string}
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
            <div className="text-sm whitespace-pre-wrap wrap-break-word">
              {curNode as string}
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
          break;
        case MessageContentType.FunctionResultContent:
          break;
        case MessageContentType.TextContent:
          break;
        case MessageContentType.TextReasoningContent:
          curNode += content;
          break;
        case MessageContentType.UriContent:
          curNode += content;
          break;
        case MessageContentType.UsageContent:
          curNode = "";
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
    return (
      <div className="text-sm whitespace-pre-wrap wrap-break-word">
        not support content type
      </div>
    );
  };

  return (
    <div className={`flex ${isUser ? "justify-end" : "justify-start"}`}>
      <div
        className={`max-w-[80%] rounded-lg px-4 py-3 ${
          isUser
            ? "bg-primary text-primary-foreground ml-12"
            : "bg-secondary mr-12"
        }`}
      >
        <div className="flex items-center gap-2 mb-1">
          <span className="text-xs font-semibold opacity-70">
            {isUser ? "You" : message.author || message.role}
          </span>
        </div>
        <div className="text-sm whitespace-pre-wrap wrap-break-word">
          {message.contents.map((content) => content.content).join("\n")}
        </div>
      </div>
    </div>
  );
};
