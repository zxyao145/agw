import { AiMessage, MessageContentType } from "@agw/api";
import { MessageNode } from "../types";
import UriContent from "./uri-content";
import Reasoning from "./reasoning";
import TextContent from "./text-content";
import SystemMessage from "./system-message";
import { isResultMessage } from "../../../../lib/chat/ai-message-handlers";
import PlanCard from "./plan-card";
import DataContent from "./data-content";

const renderContent = (node: MessageNode, msg: AiMessage): React.ReactNode => {
  if (node.proposedPlan) {
    return <PlanCard {...node.proposedPlan} />;
  }

  if (node.type === MessageContentType.DataContent) {
    return <DataContent node={node} />;
  }

  const isTextNode = (
    [
      MessageContentType.TextContent,
      MessageContentType.FunctionCallContent,
      MessageContentType.FunctionResultContent,

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
