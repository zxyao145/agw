import {
  MessageContentType,
  stripUsageContents,
  type AiMessage,
  type AiMessageContent,
} from "@agw/api";
import { getStreamingIdentity, isResultMessage, processMessages } from "@agw/execution-core";

import { parseClaudeInitCommands } from "./claude-commands";
import {
  getAgentflowCheckpointMessage,
  getHumanInteractionQuestionResult,
  matchesHumanInteractionCall,
  type AgentflowCheckpointAvailability,
  type AgentflowCheckpointMessage,
  type HumanInteractionQuestionResult,
  type PendingHumanGate,
} from "./human-interaction";
import {
  collapseConsecutiveSystemMessages,
  formatSystemMessageContent,
  getClaudeHookEventName,
  getMessageMeta,
  getMessagePreview,
  type MessageMeta,
} from "./message-presentation";
import { parseMessageProposedPlan, type ProposedPlanPresentation } from "./proposed-plan";

const HIDDEN_CONTROL_TYPES = new Set([
  "turn-start",
  "turn-finished",
  "mode-status",
  "mode-change-failed",
  "human-gate-request",
  "tool-approval-request",
  "human-interaction-request",
]);

const supportedImageDataUrl = /^data:image\/(?:jpeg|png|gif|webp);base64,/i;

export type ConversationAlignment = "left" | "right";
export type ConversationWidth = "normal" | "full";
export type ToolStatePresentationType = "todo" | "mode" | "background" | "warning";

export type PresentedContent =
  | { type: "markdown"; markdown: string; sourceType: string }
  | { type: "plain"; text: string; sourceType: string }
  | { type: "error"; text: string }
  | { type: "reasoning"; markdown: string; preview: string }
  | { type: "image"; uri: string; name: string | null }
  | { type: "uri"; uri: string; name: string | null }
  | ({ type: "plan" } & ProposedPlanPresentation);

export type PresentedMessage = {
  source: AiMessage;
  identity: string;
  alignment: ConversationAlignment;
  width: ConversationWidth;
  meta: MessageMeta | null;
  contents: PresentedContent[];
};

type BaseConversationRenderItem = {
  key: string;
  alignment: ConversationAlignment;
  width: ConversationWidth;
};

export type ConversationRenderItem =
  | (BaseConversationRenderItem & { type: "message"; message: PresentedMessage })
  | (BaseConversationRenderItem & { type: "result"; message: PresentedMessage })
  | (BaseConversationRenderItem & { type: "plan"; message: PresentedMessage })
  | (BaseConversationRenderItem & {
      type: "tool-state";
      stateType: ToolStatePresentationType;
      message: AiMessage;
    })
  | (BaseConversationRenderItem & {
      type: "tool-accordion";
      toolName: string;
      messages: PresentedMessage[];
    })
  | (BaseConversationRenderItem & {
      type: "human-interaction";
      request: PendingHumanGate;
      embedded: boolean;
    })
  | (BaseConversationRenderItem & {
      type: "human-interaction-result";
      result: HumanInteractionQuestionResult;
    })
  | (BaseConversationRenderItem & {
      type: "checkpoint";
      checkpoint: AgentflowCheckpointMessage;
      availability: AgentflowCheckpointAvailability | null;
    });

export type BuildConversationRenderModelOptions = {
  pendingHumanGate?: PendingHumanGate | null;
  checkpointAvailability?: readonly AgentflowCheckpointAvailability[];
};

export function isHiddenControlMessage(message: AiMessage): boolean {
  const type = String(message.additionalProperties?.type ?? "");
  return (
    HIDDEN_CONTROL_TYPES.has(type) ||
    message.additionalProperties?.presentation === "control" ||
    (type === "tool-mode-status" && message.additionalProperties?.toolName === "mode_get") ||
    parseClaudeInitCommands(message).isInit
  );
}

export function prepareVisibleMessages(messages: readonly AiMessage[]): AiMessage[] {
  return collapseConsecutiveSystemMessages(
    stripUsageContents([...messages]).filter((message) => !isHiddenControlMessage(message)),
  );
}

export function isSupportedImageDataUrl(value: string): boolean {
  return supportedImageDataUrl.test(value);
}

export function getToolStatePresentationType(message: AiMessage): ToolStatePresentationType | null {
  const type = message.additionalProperties?.type;
  if (type === "tool-todo-snapshot") return "todo";
  if (type === "tool-mode-status") return "mode";
  if (type === "tool-background-task-status") return "background";
  if (type === "tool-warning") return "warning";
  return null;
}

export function stringifyContentValue(value: unknown): string {
  if (typeof value === "string") return value;
  if (value == null) return "";
  return JSON.stringify(value, null, 2) ?? "";
}

export function formatToolContent(value: unknown): string {
  const original = stringifyContentValue(value);
  if (original.trim().length === 0) return original;

  let parsed = value;
  if (typeof value === "string") {
    try {
      parsed = JSON.parse(value.trim());
    } catch {
      return value;
    }
  }

  if (typeof parsed !== "object" || parsed === null) return original;
  return `\n\`\`\`json\n${JSON.stringify(parsed, null, 2)}\n\`\`\``;
}

export function presentMessage(message: AiMessage): PresentedMessage | null {
  const result = isResultMessage(message);
  const contents = message.contents.flatMap((content) => presentContent(message, content));
  if (contents.length === 0) return null;
  const hasPlan = contents.some((content) => content.type === "plan");
  const hasToolResult = message.contents.some(
    (content) => content.type === MessageContentType.FunctionResultContent,
  );
  const alignment = message.role === "user" && !hasToolResult ? "right" : "left";
  return {
    source: message,
    identity: getStreamingIdentity(message),
    alignment,
    width: result || hasPlan ? "full" : "normal",
    meta: result ? null : getMessageMeta(message),
    contents,
  };
}

export function buildConversationRenderModel(
  messages: readonly AiMessage[],
  options: BuildConversationRenderModelOptions = {},
): ConversationRenderItem[] {
  const processed = processMessages(prepareVisibleMessages(messages));
  const availability = new Map(
    (options.checkpointAvailability ?? []).map((item) => [item.occurrenceId, item]),
  );
  const occurrences = new Map<string, number>();
  const items: ConversationRenderItem[] = [];
  let embeddedInteraction = false;

  const uniqueKey = (base: string) => {
    const occurrence = occurrences.get(base) ?? 0;
    occurrences.set(base, occurrence + 1);
    return `${base}:${occurrence}`;
  };

  for (const item of processed) {
    if (item.type === "accordion") {
      const interactionResult =
        item.toolName === "ask_user_question"
          ? getHumanInteractionQuestionResult(item.messages)
          : null;
      const first = item.messages[0];
      const callId = first?.contents[0]?.additionalProperties?.callId;
      const base = `tool:${first ? getStreamingIdentity(first) : "unknown"}:${String(
        callId ?? item.toolName,
      )}`;
      if (interactionResult) {
        items.push({
          type: "human-interaction-result",
          key: uniqueKey(`interaction-result:${base}`),
          alignment: "left",
          width: "full",
          result: interactionResult,
        });
        continue;
      }

      const presentedMessages = item.messages.flatMap((message) => {
        const presented = presentMessage(message);
        return presented ? [presented] : [];
      });
      if (presentedMessages.length > 0) {
        items.push({
          type: "tool-accordion",
          key: uniqueKey(base),
          alignment: "left",
          width: "normal",
          toolName: item.toolName || "Tool",
          messages: presentedMessages,
        });
      }
      continue;
    }

    const message = item.message;
    const toolState = getToolStatePresentationType(message);
    if (toolState) {
      items.push({
        type: "tool-state",
        key: uniqueKey(`tool-state:${getStreamingIdentity(message)}`),
        alignment: "left",
        width: "normal",
        stateType: toolState,
        message,
      });
      continue;
    }
    const checkpoint = getAgentflowCheckpointMessage(message);
    if (checkpoint) {
      items.push({
        type: "checkpoint",
        key: uniqueKey(`checkpoint:${checkpoint.occurrenceId}`),
        alignment: "left",
        width: "full",
        checkpoint,
        availability: availability.get(checkpoint.occurrenceId) ?? null,
      });
      continue;
    }

    if (
      options.pendingHumanGate?.requestType === "human-interaction" &&
      matchesHumanInteractionCall(message, options.pendingHumanGate)
    ) {
      embeddedInteraction = true;
      items.push({
        type: "human-interaction",
        key: uniqueKey(`interaction:${options.pendingHumanGate.requestId}`),
        alignment: "left",
        width: "full",
        request: options.pendingHumanGate,
        embedded: true,
      });
      continue;
    }

    const presented = presentMessage(message);
    if (!presented) continue;
    const type =
      item.type === "result" || isResultMessage(message)
        ? "result"
        : presented.contents.some((content) => content.type === "plan")
          ? "plan"
          : "message";
    items.push({
      type,
      key: uniqueKey(`${type}:${presented.identity}`),
      alignment: presented.alignment,
      width: presented.width,
      message: presented,
    });
  }

  if (options.pendingHumanGate && !embeddedInteraction) {
    items.push({
      type: "human-interaction",
      key: uniqueKey(`interaction:${options.pendingHumanGate.requestId}`),
      alignment: "left",
      width: "full",
      request: options.pendingHumanGate,
      embedded: false,
    });
  }
  return items;
}

function presentContent(message: AiMessage, content: AiMessageContent): PresentedContent[] {
  if (content.type === MessageContentType.UsageContent) return [];
  if (content.type === MessageContentType.DataContent) {
    const uri = content.uri ?? "";
    return isSupportedImageDataUrl(uri)
      ? [{ type: "image", uri, name: content.name?.trim() || null }]
      : [];
  }
  if (content.type === MessageContentType.UriContent) {
    const uri = content.uri?.trim() || stringifyContentValue(content.content).trim();
    return uri ? [{ type: "uri", uri, name: content.name?.trim() || null }] : [];
  }

  const raw = stringifyContentValue(content.content);
  const hookEvent = getClaudeHookEventName(raw);
  if (hookEvent) return [{ type: "plain", text: hookEvent, sourceType: content.type }];
  if (content.type === MessageContentType.ErrorContent) {
    return [{ type: "error", text: raw || "Execution error" }];
  }
  if (content.type === MessageContentType.TextReasoningContent) {
    return raw.trim()
      ? [{ type: "reasoning", markdown: raw, preview: getMessagePreview(raw) }]
      : [];
  }
  if (
    content.type === MessageContentType.FunctionCallContent ||
    content.type === MessageContentType.FunctionResultContent
  ) {
    const markdown = formatToolContent(content.content);
    return markdown.trim() ? [{ type: "markdown", markdown, sourceType: content.type }] : [];
  }
  if (message.additionalProperties?.type === "turn.started") {
    return raw.trim() ? [{ type: "plain", text: raw, sourceType: content.type }] : [];
  }

  const markdown =
    message.role === "system" && !isResultMessage(message)
      ? formatSystemMessageContent(raw)
      : raw.startsWith("<local-command-stdout>")
        ? raw.replace("<local-command-stdout>", "").replace("</local-command-stdout>", "")
        : raw;
  if (!markdown.trim()) return [];
  const plan = parseMessageProposedPlan(message, content.type, markdown);
  return plan
    ? [{ type: "plan", ...plan }]
    : [{ type: "markdown", markdown, sourceType: content.type }];
}
