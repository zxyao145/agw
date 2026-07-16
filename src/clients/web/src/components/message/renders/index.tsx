import { AiMessage, MessageContentType } from "@/types";
import { MessageNode } from "../types";
import UriContent from "./uri-content";
import Reasoning from "./reasoning";
import TextContent from "./text-content";
import SystemMessage from "./system-message";

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
    return <SystemMessage node={node} />;
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
