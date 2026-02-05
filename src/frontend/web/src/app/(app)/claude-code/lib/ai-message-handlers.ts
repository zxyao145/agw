import { InitMessageContent } from "../types";
import { AiMessage, MessageContentType } from "@/types";

export type AiMessageAction =
  | { type: "append"; message: AiMessage }
  | { type: "setInitContent"; content: InitMessageContent }
  | { type: "setIsExecuting"; value: boolean }
  | { type: "setIsInitStatus"; value: boolean }
  | { type: "notify"; variant: "info" | "error"; message: string };

export type AiMessageContext = {
  isInitStatus: boolean;
};

export const handleSystemMessage = (
  data: AiMessage,
  context: AiMessageContext,
): AiMessageAction[] => {
  console.log("handleSystem", data);

  const actions: AiMessageAction[] = [];

  const author = data.author;
  if (author === "d-system") {
    const firstContent = data.contents[0];
    if (firstContent.type === MessageContentType.ErrorContent) {
      console.error("something error:", firstContent.content);
      actions.push({ type: "setIsExecuting", value: false });
      actions.push({ type: "append", message: data });
      return actions;
    }
  }

  if (!data.additionalProperties && !context.isInitStatus) {
    actions.push({ type: "append", message: data });
    return actions;
  }

  let msgType = "";
  if (data.additionalProperties?.subtype === "init") {
    msgType = "init";
  } else if (data.additionalProperties?.type === "result") {
    msgType = "result";
  } else if (data.additionalProperties?.subtype === "hint") {
    const hintContent = data.contents[0];
    if (
      hintContent?.type === MessageContentType.TextContent &&
      hintContent.content.toLowerCase().includes("interrupted")
    ) {
      actions.push({ type: "setIsExecuting", value: false });
    }
    console.log("hint", data);
    return actions;
  }

  if (msgType === "init") {
    const content = JSON.parse(data.contents[0].content);
    const initContent: InitMessageContent = {
      claudeCodeVersion: content.claude_code_version,
      permissionMode: content.permissionMode,
      model: content.model,
      tools: content.tools,
      slashCommands: content.slash_commands,
      agents: content.agents,
      skills: content.skills,
      plugins: content.plugins,
      mcpServers: content.mcp_servers,
    };

    actions.push({ type: "setInitContent", content: initContent });
  } else if (msgType === "result") {
    actions.push({
      type: "notify",
      variant: "info",
      message: "Execution completed",
    });
    actions.push({ type: "setIsExecuting", value: false });
    if (context.isInitStatus) {
      actions.push({ type: "setIsInitStatus", value: false });
    }
  } else {
    actions.push({ type: "append", message: data });
  }

  return actions;
};

export const handleUserMessage = (data: AiMessage): AiMessageAction[] => [
  { type: "append", message: data },
];

export const handleAssistantMessage = (data: AiMessage): AiMessageAction[] => [
  { type: "append", message: data },
];

export const handleAiMessage = (
  data: AiMessage,
  context: AiMessageContext,
): AiMessageAction[] => {
  if (data.role === "system") {
    return handleSystemMessage(data, context);
  }
  if (data.role === "assistant") {
    return handleAssistantMessage(data);
  }
  if (data.role === "user") {
    return handleUserMessage(data);
  }
  return [];
};
