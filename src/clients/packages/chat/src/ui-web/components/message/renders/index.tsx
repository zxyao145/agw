import { AiMessage, MessageContentType } from "@agw/api";
import { MessageNode } from "../types";
import UriContent from "./uri-content";
import Reasoning from "./reasoning";
import TextContent from "./text-content";
import SystemMessage from "./system-message";
import { isResultMessage } from "../../../../lib/chat/ai-message-handlers";

const renderContent = (node: MessageNode, msg: AiMessage): React.ReactNode => {
  const isTextNode = (
    [
      MessageContentType.TextContent,
      MessageContentType.FunctionCallContent,
      MessageContentType.FunctionResultContent,

      MessageContentType.DataContent,
      MessageContentType.ErrorContent,
    ] as string[]
  ).includes(node.type);

  const isSystem = msg.role === "system";
  if (isSystem) {
    const isResult = isResultMessage(msg);
    if (!isResult) {
      return <SystemMessage node={node} />;
    }
    return <TextContent node={node} />;
  }

  if (isTextNode) {
    return <TextContent node={node} />;
  }

  if (node.type === MessageContentType.TextReasoningContent) {
    return <Reasoning node={node} />;
  }

  if (node.type === MessageContentType.UriContent) {
    return <UriContent node={node} />;
  }

  return null;
};

export { renderContent };
