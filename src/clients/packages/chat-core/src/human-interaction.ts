import { MessageContentType, type AiMessage } from "@agw/api";
import { getMessageStreamingScopeId } from "@agw/execution-core";

export type HumanInteractionQuestionOption = {
  label: string;
  description: string;
  preview?: string;
};

export type HumanInteractionQuestion = {
  question: string;
  header: string;
  options: HumanInteractionQuestionOption[];
  multiSelect: boolean;
};

export type HumanInteractionQuestionAnnotation = { preview?: string; notes?: string };
export type HumanInteractionQuestionResponse = {
  answers: Record<string, string>;
  annotations?: Record<string, HumanInteractionQuestionAnnotation>;
};
export type HumanInteractionModeChange = { mode: "plan" | "execute" };
export type HumanInteractionModeChangeResponse = { confirmed: true };
export type HumanInteractionQuestionResultItem = { question: string; answer: string | null };
export type HumanInteractionQuestionResult = {
  cancelled: boolean;
  items: HumanInteractionQuestionResultItem[];
};

export type PendingHumanGate = {
  requestType: "human-gate" | "tool-approval" | "human-interaction";
  requestId: string;
  nodeId?: string;
  nodeName?: string;
  mode: string;
  prompt: string;
  inputPreview?: string;
  toolName?: string;
  callId?: string;
  streamingScopeId?: string;
  arguments?: string;
  interactionKind?: string;
  questions?: HumanInteractionQuestion[];
  modeChange?: HumanInteractionModeChange;
};

export type AgentflowCheckpointMessage = { occurrenceId: string; nodeId: string; name: string };
export type AgentflowCheckpointMarkerInfo = { nodeId: string; name: string; messageId: string };
export type AgentflowCheckpointAvailability = {
  occurrenceId: string;
  agentflowId: string;
  boundarySequence: number;
  available: boolean;
  markers: AgentflowCheckpointMarkerInfo[];
};

export const OTHER_OPTION_VALUE = "__agw_other_option__";
export type HumanInteractionQuestionSelection = {
  selected: string[];
  otherSelected: boolean;
  otherText: string;
};
export type HumanInteractionQuestionSelections = Record<string, HumanInteractionQuestionSelection>;

export function parseHumanInteractionQuestions(
  payload: unknown,
): HumanInteractionQuestion[] | null {
  if (!isRecord(payload) || !Array.isArray(payload.questions)) return null;
  if (payload.questions.length < 1 || payload.questions.length > 4) return null;

  const questions: HumanInteractionQuestion[] = [];
  const questionTexts = new Set<string>();
  for (const value of payload.questions) {
    if (!isRecord(value)) return null;
    const question = readRequiredString(value.question);
    const header = readRequiredString(value.header);
    if (!question || !header || questionTexts.has(question) || !Array.isArray(value.options)) {
      return null;
    }
    if (value.options.length < 2 || value.options.length > 4) return null;

    const options: HumanInteractionQuestionOption[] = [];
    const optionLabels = new Set<string>();
    for (const optionValue of value.options) {
      if (!isRecord(optionValue)) return null;
      const label = readRequiredString(optionValue.label);
      const description = readRequiredString(optionValue.description);
      if (!label || !description || optionLabels.has(label)) return null;
      optionLabels.add(label);
      const preview = readOptionalString(optionValue.preview);
      options.push({ label, description, ...(preview ? { preview } : {}) });
    }

    questionTexts.add(question);
    questions.push({ question, header, options, multiSelect: value.multiSelect === true });
  }
  return questions;
}

export function parseHumanInteractionModeChange(
  payload: unknown,
): HumanInteractionModeChange | null {
  return isRecord(payload) && (payload.mode === "plan" || payload.mode === "execute")
    ? { mode: payload.mode }
    : null;
}

export function getPendingHumanGate(message: AiMessage): PendingHumanGate | null {
  const properties = message.additionalProperties;
  if (!properties) return null;
  const requestType = properties.type;
  const requestId = readOptionalString(properties.requestId);
  if (!requestId) return null;

  if (requestType === "human-interaction-request") {
    const interactionKind = readOptionalString(properties.interactionKind);
    if (!interactionKind) return null;
    const questions =
      interactionKind === "questions"
        ? (parseHumanInteractionQuestions(properties.payload) ?? undefined)
        : undefined;
    const modeChange =
      interactionKind === "mode-change"
        ? (parseHumanInteractionModeChange(properties.payload) ?? undefined)
        : undefined;
    const streamingScopeId = getMessageStreamingScopeId(message);
    return {
      requestType: "human-interaction",
      requestId,
      mode: "interaction",
      interactionKind,
      prompt:
        readOptionalString(properties.prompt) ??
        readOptionalString(message.contents[0]?.content) ??
        "The agent needs your input to continue.",
      toolName: readOptionalString(properties.toolName),
      callId: readOptionalString(properties.callId),
      ...(streamingScopeId ? { streamingScopeId } : {}),
      ...(questions ? { questions } : {}),
      ...(modeChange ? { modeChange } : {}),
    };
  }

  if (requestType !== "human-gate-request" && requestType !== "tool-approval-request") return null;
  const nodeId = readOptionalString(properties.nodeId);
  if (!nodeId) return null;
  return {
    requestType: requestType === "tool-approval-request" ? "tool-approval" : "human-gate",
    requestId,
    nodeId,
    nodeName: readOptionalString(properties.nodeName),
    mode: readOptionalString(properties.mode) ?? "approval",
    prompt:
      readOptionalString(properties.prompt) ??
      readOptionalString(message.contents[0]?.content) ??
      "Human approval is required to continue.",
    inputPreview: readOptionalString(properties.inputPreview),
    toolName: readOptionalString(properties.toolName),
    arguments: readOptionalString(properties.arguments),
  };
}

export function getAgentflowCheckpointMessage(
  message: AiMessage,
): AgentflowCheckpointMessage | null {
  const properties = message.additionalProperties;
  if (properties?.type !== "agentflow-checkpoint") return null;
  const occurrenceId = readOptionalString(properties.checkpointOccurrenceId);
  const nodeId = readOptionalString(properties.checkpointNodeId);
  if (!occurrenceId || !nodeId) return null;
  return {
    occurrenceId,
    nodeId,
    name:
      readOptionalString(properties.checkpointName) ??
      readOptionalString(message.contents[0]?.content) ??
      "Checkpoint",
  };
}

export function matchesHumanInteractionCall(
  message: AiMessage,
  target: Pick<PendingHumanGate, "callId" | "streamingScopeId">,
): boolean {
  if (!target.callId) return false;
  if (target.streamingScopeId && message.streamingScopeId !== target.streamingScopeId) return false;
  return message.contents.some(
    (content) =>
      content.type === MessageContentType.FunctionCallContent &&
      content.additionalProperties?.callId === target.callId,
  );
}

export function hasMatchingHumanInteractionCall(
  messages: readonly AiMessage[],
  target: Pick<PendingHumanGate, "callId" | "streamingScopeId">,
): boolean {
  return messages.some((message) => matchesHumanInteractionCall(message, target));
}

export function getHumanInteractionQuestionResult(
  messages: readonly AiMessage[],
): HumanInteractionQuestionResult | null {
  for (let messageIndex = messages.length - 1; messageIndex >= 0; messageIndex -= 1) {
    const contents = messages[messageIndex]?.contents ?? [];
    for (let contentIndex = contents.length - 1; contentIndex >= 0; contentIndex -= 1) {
      const content = contents[contentIndex];
      if (content?.type === MessageContentType.FunctionResultContent) {
        return parseHumanInteractionQuestionResult(content.content);
      }
    }
  }
  return null;
}

export function parseHumanInteractionQuestionResult(
  value: unknown,
): HumanInteractionQuestionResult | null {
  const result = parseRecord(value);
  if (!result || !Array.isArray(result.questions)) return null;
  if (result.questions.length < 1 || result.questions.length > 4) return null;
  const cancelled = result.cancelled === true;
  const answers = isRecord(result.answers) ? result.answers : null;
  if (!cancelled && !answers) return null;

  const items: HumanInteractionQuestionResultItem[] = [];
  const questionTexts = new Set<string>();
  for (const questionValue of result.questions) {
    if (!isRecord(questionValue)) return null;
    const question = readRequiredString(questionValue.question);
    if (!question || questionTexts.has(question)) return null;
    const answer = cancelled ? null : readRequiredString(answers?.[question]);
    if (!cancelled && !answer) return null;
    questionTexts.add(question);
    items.push({ question, answer });
  }
  return { cancelled, items };
}

export function createQuestionSelections(
  questions: readonly HumanInteractionQuestion[],
): HumanInteractionQuestionSelections {
  return Object.fromEntries(
    questions.map((question) => [
      question.question,
      { selected: [], otherSelected: false, otherText: "" },
    ]),
  );
}

export function buildQuestionResponse(
  questions: readonly HumanInteractionQuestion[],
  selections: HumanInteractionQuestionSelections,
): HumanInteractionQuestionResponse | null {
  const answers: Record<string, string> = {};
  const annotations: NonNullable<HumanInteractionQuestionResponse["annotations"]> = {};
  for (const question of questions) {
    const selection = selections[question.question];
    if (!selection) return null;
    const selectedLabels = question.options
      .map((option) => option.label)
      .filter((label) => selection.selected.includes(label));
    const otherText = selection.otherSelected ? selection.otherText.trim() : "";
    const values = [...selectedLabels, ...(otherText ? [otherText] : [])];
    if (values.length === 0) return null;
    answers[question.question] = question.multiSelect ? values.join(", ") : values[0]!;
    if (!question.multiSelect && selectedLabels.length === 1) {
      const preview = question.options.find(
        (option) => option.label === selectedLabels[0],
      )?.preview;
      if (preview) annotations[question.question] = { preview };
    }
  }
  return {
    answers,
    ...(Object.keys(annotations).length > 0 ? { annotations } : {}),
  };
}

function parseRecord(value: unknown): Record<string, unknown> | null {
  if (isRecord(value)) return value;
  if (typeof value !== "string") return null;
  try {
    const parsed: unknown = JSON.parse(value);
    return isRecord(parsed) ? parsed : null;
  } catch {
    return null;
  }
}

function isRecord(value: unknown): value is Record<string, unknown> {
  return typeof value === "object" && value !== null && !Array.isArray(value);
}

function readRequiredString(value: unknown): string | null {
  return typeof value === "string" && value.trim().length > 0 ? value.trim() : null;
}

function readOptionalString(value: unknown): string | undefined {
  return typeof value === "string" && value.trim().length > 0 ? value.trim() : undefined;
}
