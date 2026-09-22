import {
  MessageContentType,
  stripUsageContents,
  type AiMessage,
  type AiMessageContent,
} from "@agw/api";
import {
  getStreamingIdentity,
  isResultMessage,
  isUserTurnMessage,
  processMessages,
} from "@agw/execution-core";

import { parseClaudeInitCommands } from "./claude-commands";
import {
  getAgentflowCheckpointMessage,
  getHumanInteractionQuestionResult,
  matchesHumanInteractionCall,
  type AgentflowCheckpointAvailability,
  type AgentflowCheckpointMessage,
  type HumanInteractionQuestionResult,
  type PendingInteraction,
} from "./human-interaction";
import {
  collapseConsecutiveSystemMessages,
  formatSystemMessageContent,
  getClaudeHookEventName,
  getClaudeSystemEventName,
  getMessageMeta,
  getMessagePreview,
  type MessageMeta,
} from "./message-presentation";
import { isSystemInjectedMessage } from "./message-source";
import { parseMessageProposedPlan, type ProposedPlanPresentation } from "./proposed-plan";
import { formatStructuredResult, normalizeStructuredResult } from "./structured-result";
import { collapseCompletedWork } from "./work-summary";

const HIDDEN_CONTROL_TYPES = new Set([
  "turn-start",
  "turn-finished",
  "mode-status",
  "mode-change-failed",
  "interaction-request",
]);

const supportedImageDataUrl = /^data:image\/(?:jpeg|png|gif|webp);base64,/i;
const HIDDEN_SYSTEM_TOOL_NAMES = new Set(["Skill", "load_skill", "read_skill_resource"]);
const TODO_TOOL_NAMES = new Set([
  "todos_add",
  "todos_complete",
  "todos_remove",
  "todos_get_remaining",
  "todos_get_all",
]);
const QUESTION_INTERACTION_TOOL_NAMES = new Set(["ask_user_question", "AskUserQuestion"]);
const CLAUDE_CODE_AGENT_NAME = "claude-code";
const CLAUDE_SESSION_START_EVENT = "SessionStart";

function getToolGroupKey(message: AiMessage, content: AiMessageContent): string | null {
  const callId = content.additionalProperties?.callId;
  return typeof callId === "string" && callId.length > 0
    ? JSON.stringify([message.streamingScopeId ?? null, callId])
    : null;
}

function getToolStateGroupKey(message: AiMessage): string | null {
  const callId = message.additionalProperties?.callId;
  return typeof callId === "string" && callId.length > 0
    ? JSON.stringify([message.streamingScopeId ?? null, callId])
    : null;
}

function getTodoItemIdentity(item: unknown): string | null {
  if (typeof item !== "object" || item === null || !("id" in item)) return null;
  const id = item.id;
  return typeof id === "string" || typeof id === "number" ? String(id) : null;
}

function readTodoItems(value: unknown): TodoPresentationItem[] {
  if (!Array.isArray(value)) return [];
  return value.filter(
    (item): item is TodoPresentationItem =>
      typeof item === "object" &&
      item !== null &&
      "id" in item &&
      (typeof item.id === "string" || typeof item.id === "number") &&
      "title" in item &&
      typeof item.title === "string" &&
      "isComplete" in item &&
      typeof item.isComplete === "boolean",
  );
}

function getHiddenSystemToolKeys(messages: readonly AiMessage[]): Set<string> {
  const keys = new Set<string>();
  for (const message of messages) {
    for (const content of message.contents) {
      if (
        content.type !== MessageContentType.FunctionCallContent ||
        !HIDDEN_SYSTEM_TOOL_NAMES.has(String(content.additionalProperties?.toolName ?? ""))
      ) {
        continue;
      }

      const key = getToolGroupKey(message, content);
      if (key) keys.add(key);
    }
  }
  return keys;
}

function isHiddenSystemToolFragment(
  message: AiMessage,
  hiddenToolKeys: ReadonlySet<string>,
): boolean {
  return message.contents.some((content) => {
    if (
      content.type !== MessageContentType.FunctionCallContent &&
      content.type !== MessageContentType.FunctionResultContent
    ) {
      return false;
    }

    if (
      content.type === MessageContentType.FunctionCallContent &&
      HIDDEN_SYSTEM_TOOL_NAMES.has(String(content.additionalProperties?.toolName ?? ""))
    ) {
      return true;
    }

    const key = getToolGroupKey(message, content);
    return key !== null && hiddenToolKeys.has(key);
  });
}

export type ConversationAlignment = "left" | "right";
export type ConversationWidth = "normal" | "full";
export type ToolStatePresentationType = "todo" | "mode" | "background" | "warning";
export type ToolCallStatus = "running" | "complete" | "failed";
export type TodoPresentationItem = {
  id: string | number;
  title: string;
  description?: string | null;
  isComplete: boolean;
};

export type PresentedContent =
  | { type: "markdown"; markdown: string; sourceType: string }
  | { type: "json"; text: string }
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

export type PresentedTool = {
  identity: string;
  scopeId: string | null;
  toolName: string;
  summary: string | null;
  status: ToolCallStatus;
  messages: PresentedMessage[];
};

type BaseConversationRenderItem = {
  key: string;
  alignment: ConversationAlignment;
  width: ConversationWidth;
};

export type ConversationMessageRenderItem =
  | (BaseConversationRenderItem & { type: "message"; message: PresentedMessage })
  | (BaseConversationRenderItem & {
      type: "result";
      message: PresentedMessage;
      hasWorkSummary?: boolean;
    })
  | (BaseConversationRenderItem & { type: "plan"; message: PresentedMessage })
  | (BaseConversationRenderItem & {
      type: "tool-state";
      stateType: ToolStatePresentationType;
      message: AiMessage;
    })
  | (BaseConversationRenderItem & { type: "tool-accordion" } & PresentedTool)
  | (BaseConversationRenderItem & {
      type: "tool-batch";
      tools: PresentedTool[];
    })
  | (BaseConversationRenderItem & {
      type: "human-interaction";
      request: PendingInteraction;
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

export type ConversationRenderItem =
  | ConversationMessageRenderItem
  | (BaseConversationRenderItem & {
      type: "work-summary";
      durationMs: number | null;
      items: Exclude<ConversationMessageRenderItem, { type: "result" }>[];
    });

export type BuildConversationRenderModelOptions = {
  /** Keep the current turn visible while executing, awaiting input, or restoring a connection. */
  isCurrentTurnActive?: boolean;
  pendingInteraction?: PendingInteraction | null;
  checkpointAvailability?: readonly AgentflowCheckpointAvailability[];
  collapseToolRuns?: boolean;
  activeAgentId?: string | null;
  agentResultFormats?: readonly AgentResultFormat[];
};

export type ResultFormat = import("@agw/api").components["schemas"]["ResultFormat"];
export type AgentResultFormat = { id: string; resultFormat?: ResultFormat };

export function isHiddenControlMessage(message: AiMessage): boolean {
  const type = String(message.additionalProperties?.type ?? "");
  return (
    HIDDEN_CONTROL_TYPES.has(type) ||
    isSystemInjectedMessage(message) ||
    message.additionalProperties?.presentation === "control" ||
    (type === "tool-mode-status" && message.additionalProperties?.toolName === "mode_get") ||
    parseClaudeInitCommands(message).isInit
  );
}

function isClaudeCodeSystemMessage(message: AiMessage): boolean {
  const agentName = message.additionalProperties?.agentName;
  return (
    message.role === "system" &&
    typeof agentName === "string" &&
    agentName.trim().toLowerCase() === CLAUDE_CODE_AGENT_NAME
  );
}

function shouldShowClaudeCodeSystemMessage(message: AiMessage): boolean {
  if (!isClaudeCodeSystemMessage(message) || isResultMessage(message)) return true;
  if (message.additionalProperties?.subtype === "api_retry") return true;

  return message.contents.some(
    (content) =>
      getClaudeSystemEventName(stringifyContentValue(content.content)) ===
      CLAUDE_SESSION_START_EVENT,
  );
}

export function prepareVisibleMessages(messages: readonly AiMessage[]): AiMessage[] {
  return collapseConsecutiveSystemMessages(
    stripUsageContents([...messages]).filter(
      (message) => !isHiddenControlMessage(message) && shouldShowClaudeCodeSystemMessage(message),
    ),
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

const TOOL_SUMMARY_KEYS = [
  "description",
  "command",
  "query",
  "file_path",
  "filePath",
  "path",
  "url",
];

function getToolCallContent(messages: readonly AiMessage[]): AiMessageContent | undefined {
  return messages
    .flatMap((message) => message.contents)
    .find((content) => content.type === MessageContentType.FunctionCallContent);
}

function parseToolArguments(value: unknown): Record<string, unknown> | null {
  let parsed = value;
  if (typeof value === "string") {
    try {
      parsed = JSON.parse(value);
    } catch {
      return null;
    }
  }

  return typeof parsed === "object" && parsed !== null && !Array.isArray(parsed)
    ? (parsed as Record<string, unknown>)
    : null;
}

function normalizeToolSummary(value: unknown): string | null {
  if (typeof value !== "string") return null;
  const summary = value.replace(/\s+/g, " ").trim();
  return summary.length > 0 ? summary : null;
}

function getToolSummary(messages: readonly AiMessage[]): string | null {
  const call = getToolCallContent(messages);
  const argumentsObject = parseToolArguments(call?.content);
  if (!argumentsObject) return null;

  for (const key of TOOL_SUMMARY_KEYS) {
    const summary = normalizeToolSummary(argumentsObject[key]);
    if (summary) return summary;
  }

  return null;
}

function isToolErrorResult(content: AiMessageContent): boolean {
  if (content.additionalProperties?.isError === true) return true;
  const result = parseToolArguments(content.content);
  return result?.isError === true;
}

function getToolCallStatus(messages: readonly AiMessage[]): ToolCallStatus {
  const contents = messages.flatMap((message) => message.contents);
  const hasResult = contents.some(
    (content) => content.type === MessageContentType.FunctionResultContent,
  );
  if (!hasResult) return "running";

  const failed = contents.some(
    (content) =>
      content.type === MessageContentType.ErrorContent ||
      (content.type === MessageContentType.FunctionResultContent && isToolErrorResult(content)),
  );
  return failed ? "failed" : "complete";
}

function getToolScopeId(messages: readonly AiMessage[]): string | null {
  const scopeId = messages[0]?.streamingScopeId;
  return typeof scopeId === "string" && scopeId.length > 0 ? scopeId : null;
}

function getToolName(messages: readonly AiMessage[], fallback: string): string {
  const call = getToolCallContent(messages);
  const toolName = call?.additionalProperties?.toolName;
  return typeof toolName === "string" && toolName.trim().length > 0
    ? toolName
    : fallback.trim() || "Tool";
}

function getToolPresentationBase(message: AiMessage, content: AiMessageContent): string {
  const callId = content.additionalProperties?.callId;
  const toolName = content.additionalProperties?.toolName;
  return `tool:${getStreamingIdentity(message)}:${String(
    callId ?? (typeof toolName === "string" ? toolName : "Tool"),
  )}`;
}

function createPresentedTool(
  messages: AiMessage[],
  presentedMessages: PresentedMessage[],
  identity: string,
  fallbackToolName: string,
): PresentedTool {
  return {
    identity,
    scopeId: getToolScopeId(messages),
    toolName: getToolName(messages, fallbackToolName),
    summary: getToolSummary(messages),
    status: getToolCallStatus(messages),
    messages: presentedMessages,
  };
}

function formatJsonToolContent(value: unknown): string | null {
  const original = stringifyContentValue(value);
  if (original.trim().length === 0) return null;

  let parsed = value;
  if (typeof value === "string") {
    try {
      parsed = JSON.parse(value.trim());
    } catch {
      return null;
    }
  }

  if (typeof parsed !== "object" || parsed === null) return null;
  return `\n\`\`\`json\n${JSON.stringify(parsed, null, 2)}\n\`\`\``;
}

export function formatToolContent(value: unknown): string {
  return formatJsonToolContent(value) ?? stringifyContentValue(value);
}

// 纯文本结果包成围栏代码块，围栏长度避开内容中的反引号
function fencePlainToolText(text: string): string {
  const longestRun = Math.max(0, ...[...text.matchAll(/`+/g)].map((match) => match[0].length));
  const fence = "`".repeat(Math.max(3, longestRun + 1));
  return `\n${fence}\n${text}\n${fence}`;
}

export function formatToolResultContent(value: unknown): string {
  const json = formatJsonToolContent(value);
  if (json) return json;

  const text = stringifyContentValue(value);
  return text.trim() ? fencePlainToolText(text) : text;
}

export function presentMessage(
  message: AiMessage,
  defaultResultFormat: ResultFormat = "markdown",
): PresentedMessage | null {
  const result = isResultMessage(message);
  const contents = message.contents.flatMap((content) =>
    presentContent(message, content, defaultResultFormat),
  );
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

function projectTurnScopedTodoSnapshots(messages: readonly AiMessage[]) {
  const byGroupKey = new Map<string, AiMessage>();
  const byIdentity = new Map<string, AiMessage>();
  const latestItemsByScope = new Map<string, TodoPresentationItem[]>();
  const baselineIdsByScope = new Map<string, ReadonlySet<string>>();
  const seenTodoIds = new Set<string>();

  for (const message of messages) {
    if (getToolStatePresentationType(message) !== "todo") continue;
    const snapshotItems = readTodoItems(message.additionalProperties?.items);
    const scopeId = message.streamingScopeId ?? null;
    let visibleItems = snapshotItems;
    if (scopeId !== null) {
      let baselineIds = baselineIdsByScope.get(scopeId);
      if (!baselineIds) {
        baselineIds = new Set(seenTodoIds);
        baselineIdsByScope.set(scopeId, baselineIds);
      }
      visibleItems = snapshotItems.filter((item) => {
        const id = getTodoItemIdentity(item);
        return id === null || !baselineIds.has(id);
      });
      for (const item of snapshotItems) {
        const id = getTodoItemIdentity(item);
        if (id !== null) seenTodoIds.add(id);
      }
      latestItemsByScope.set(scopeId, visibleItems);
    }
    const scopedSnapshot: AiMessage = {
      ...message,
      additionalProperties: {
        ...message.additionalProperties,
        items: visibleItems,
      },
    };
    const groupKey = getToolStateGroupKey(message);
    if (groupKey) byGroupKey.set(groupKey, scopedSnapshot);
    byIdentity.set(getStreamingIdentity(message), scopedSnapshot);
  }

  return { byGroupKey, byIdentity, latestItemsByScope };
}

export function getCurrentTurnTodoItems(messages: readonly AiMessage[]): TodoPresentationItem[] {
  const currentScopeId = messages.findLast(
    (message) =>
      typeof message.streamingScopeId === "string" && message.streamingScopeId.length > 0,
  )?.streamingScopeId;
  if (!currentScopeId) return [];

  return (
    projectTurnScopedTodoSnapshots(prepareVisibleMessages(messages)).latestItemsByScope.get(
      currentScopeId,
    ) ?? []
  );
}

export function buildConversationRenderModel(
  messages: readonly AiMessage[],
  options: BuildConversationRenderModelOptions = {},
): ConversationRenderItem[] {
  const visibleMessages = prepareVisibleMessages(messages);
  const hiddenSystemToolKeys = getHiddenSystemToolKeys(visibleMessages);
  const processed = processMessages(visibleMessages);
  const { byGroupKey: todoSnapshotsByGroupKey, byIdentity: todoSnapshotsByIdentity } =
    projectTurnScopedTodoSnapshots(visibleMessages);
  const consumedTodoSnapshotKeys = new Set<string>();
  const availability = new Map(
    (options.checkpointAvailability ?? []).map((item) => [item.occurrenceId, item]),
  );
  const occurrences = new Map<string, number>();
  const items: ConversationMessageRenderItem[] = [];
  let embeddedInteraction = false;
  const agentResultFormats = new Map(
    (options.agentResultFormats ?? []).map(
      (agent) => [agent.id.toLowerCase(), agent.resultFormat ?? "markdown"] as const,
    ),
  );
  let resultAgentId = options.activeAgentId ?? null;

  const uniqueKey = (base: string) => {
    const occurrence = occurrences.get(base) ?? 0;
    occurrences.set(base, occurrence + 1);
    return `${base}:${occurrence}`;
  };

  for (const item of processed) {
    if (item.type === "accordion") {
      if (HIDDEN_SYSTEM_TOOL_NAMES.has(item.toolName)) continue;

      const first = item.messages.find((message) => getToolCallContent([message]));
      const call = first ? getToolCallContent([first]) : undefined;
      const base = first && call ? getToolPresentationBase(first, call) : `tool:${item.toolName}`;
      const toolGroupKey = first && call ? getToolGroupKey(first, call) : null;
      const todoSnapshot =
        TODO_TOOL_NAMES.has(item.toolName) && toolGroupKey
          ? todoSnapshotsByGroupKey.get(toolGroupKey)
          : undefined;
      if (todoSnapshot) {
        consumedTodoSnapshotKeys.add(getStreamingIdentity(todoSnapshot));
        items.push({
          type: "tool-state",
          key: uniqueKey(`tool-state:${base}`),
          alignment: "left",
          width: "normal",
          stateType: "todo",
          message: todoSnapshot,
        });
        continue;
      }

      const interactionResult = QUESTION_INTERACTION_TOOL_NAMES.has(item.toolName)
        ? getHumanInteractionQuestionResult(item.messages)
        : null;
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
        const presented = presentMessage(
          message,
          agentResultFormats.get(resultAgentId?.toLowerCase() ?? "") ?? "markdown",
        );
        return presented ? [presented] : [];
      });
      const identity = uniqueKey(base);
      items.push({
        type: "tool-accordion",
        key: identity,
        alignment: "left",
        width: "normal",
        ...createPresentedTool(item.messages, presentedMessages, identity, item.toolName),
      });
      continue;
    }

    const message = item.message;
    if (isUserTurnMessage(message)) {
      const target = [
        message.additionalProperties,
        ...message.contents.map((content) => content.additionalProperties),
      ].find((properties) => properties?.targetType !== undefined);
      resultAgentId = target
        ? target.targetType === "agent" && typeof target.targetId === "string"
          ? target.targetId
          : null
        : (options.activeAgentId ?? null);
    }
    if (isHiddenSystemToolFragment(message, hiddenSystemToolKeys)) continue;

    const toolState = getToolStatePresentationType(message);
    if (toolState) {
      if (toolState === "todo" && consumedTodoSnapshotKeys.has(getStreamingIdentity(message))) {
        continue;
      }
      const toolStateMessage =
        toolState === "todo"
          ? (todoSnapshotsByIdentity.get(getStreamingIdentity(message)) ?? message)
          : message;
      items.push({
        type: "tool-state",
        key: uniqueKey(`tool-state:${getStreamingIdentity(message)}`),
        alignment: "left",
        width: "normal",
        stateType: toolState,
        message: toolStateMessage,
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
      options.pendingInteraction?.kind === "user-input" &&
      matchesHumanInteractionCall(message, options.pendingInteraction)
    ) {
      embeddedInteraction = true;
      items.push({
        type: "human-interaction",
        key: uniqueKey(`interaction:${options.pendingInteraction.interactionId}`),
        alignment: "left",
        width: "full",
        request: options.pendingInteraction,
        embedded: true,
      });
      continue;
    }

    const toolCall = getToolCallContent([message]);
    if (toolCall && options.collapseToolRuns) {
      const identity = uniqueKey(getToolPresentationBase(message, toolCall));
      const presented = presentMessage(
        message,
        agentResultFormats.get(resultAgentId?.toLowerCase() ?? "") ?? "markdown",
      );
      items.push({
        type: "tool-accordion",
        key: identity,
        alignment: "left",
        width: "normal",
        ...createPresentedTool(
          [message],
          presented ? [presented] : [],
          identity,
          getToolName([message], "Tool"),
        ),
      });
      continue;
    }

    const presented = presentMessage(
      message,
      agentResultFormats.get(resultAgentId?.toLowerCase() ?? "") ?? "markdown",
    );
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

  if (options.pendingInteraction && !embeddedInteraction) {
    items.push({
      type: "human-interaction",
      key: uniqueKey(`interaction:${options.pendingInteraction.interactionId}`),
      alignment: "left",
      width: "full",
      request: options.pendingInteraction,
      embedded: false,
    });
  }
  return collapseCompletedWork(
    options.collapseToolRuns ? collapseConsecutiveToolItems(items) : items,
    options.isCurrentTurnActive === true || options.pendingInteraction != null,
  );
}

function collapseConsecutiveToolItems(
  items: ConversationMessageRenderItem[],
): ConversationMessageRenderItem[] {
  const collapsed: ConversationMessageRenderItem[] = [];

  for (let index = 0; index < items.length;) {
    const item = items[index];
    if (item.type !== "tool-accordion" || item.scopeId === null) {
      collapsed.push(item);
      index += 1;
      continue;
    }

    const run = [item];
    let nextIndex = index + 1;
    while (nextIndex < items.length) {
      const candidate = items[nextIndex];
      if (candidate.type !== "tool-accordion" || candidate.scopeId !== item.scopeId) {
        break;
      }

      run.push(candidate);
      nextIndex += 1;
    }

    if (run.length < 2) {
      collapsed.push(item);
    } else {
      collapsed.push({
        type: "tool-batch",
        key: `tool-batch:${run[0].key}`,
        alignment: "left",
        width: "normal",
        tools: run.map(({ identity, scopeId, toolName, summary, status, messages }) => ({
          identity,
          scopeId,
          toolName,
          summary,
          status,
          messages,
        })),
      });
    }

    index = nextIndex;
  }

  return collapsed;
}

function presentContent(
  message: AiMessage,
  content: AiMessageContent,
  defaultResultFormat: ResultFormat,
): PresentedContent[] {
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
  const messageResultFormat = message.additionalProperties?.resultFormat;
  const isStructuredResult = messageResultFormat === "json";
  if (
    (messageResultFormat ?? defaultResultFormat) === "json" &&
    content.type === MessageContentType.TextContent &&
    isResultMessage(message)
  ) {
    const json = normalizeStructuredResult(raw);
    // Older provider-native Results omit resultFormat; their configuration is the fallback.
    if (json !== null && (isStructuredResult || json === raw.trim())) {
      return [{ type: "json", text: formatStructuredResult(json) }];
    }
    if (isStructuredResult) {
      return [
        { type: "error", text: "Invalid structured result: expected one JSON object or array." },
      ];
    }
  }
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
    const markdown =
      content.type === MessageContentType.FunctionResultContent
        ? formatToolResultContent(content.content)
        : formatToolContent(content.content);
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
