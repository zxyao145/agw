import { MessageContentType, type AiMessage } from "@agw/api";
import {
  getMessageStreamingScopeId,
  type InteractionRequest,
  type InteractionSource,
} from "@agw/execution-core";

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

export type PendingInteraction = InteractionRequest & {
  executionId?: string;
  streamingScopeId?: string;
  questions?: HumanInteractionQuestion[];
  modeChange?: HumanInteractionModeChange;
};

export type SimpleUserInput = {
  inputKind: "confirm" | "select" | "input" | "editor";
  message?: string;
  options: string[];
  placeholder?: string;
  prefill?: string;
};

/** Pi's extension UI payload retains its SDK field names. */
export function parseSimpleUserInput(inputKind: string, payload: unknown): SimpleUserInput | null {
  if (!isRecord(payload) || !["confirm", "select", "input", "editor"].includes(inputKind))
    return null;
  return {
    inputKind: inputKind as SimpleUserInput["inputKind"],
    message: readOptionalString(payload.Message),
    options: Array.isArray(payload.Options)
      ? payload.Options.filter((value): value is string => typeof value === "string")
      : [],
    placeholder: readOptionalString(payload.Placeholder),
    prefill: typeof payload.Prefill === "string" ? payload.Prefill : undefined,
  };
}

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

export function getPendingInteraction(message: AiMessage): PendingInteraction | null {
  const properties = message.additionalProperties;
  if (properties?.type !== "interaction-request" || !isRecord(properties.interaction)) return null;
  const interaction = properties.interaction;
  const interactionId = readRequiredString(interaction.interactionId);
  if (!interactionId || typeof interaction.prompt !== "string" || !isRecord(interaction.source))
    return null;
  const source: InteractionSource = {};
  for (const key of [
    "nodeId",
    "nodeName",
    "toolName",
    "callId",
    "providerRequestId",
    "providerScopeId",
  ] as const) {
    const value = readOptionalString(interaction.source[key]);
    if (value !== undefined) source[key] = value;
  }
  const executionId = readOptionalString(properties.executionId);
  const streamingScopeId = getMessageStreamingScopeId(message);
  const common = {
    interactionId,
    prompt: interaction.prompt,
    source,
    ...(executionId ? { executionId } : {}),
    ...(streamingScopeId ? { streamingScopeId } : {}),
  };
  switch (interaction.kind) {
    case "tool-approval":
      return {
        ...common,
        kind: "tool-approval",
        ...(interaction.arguments === undefined ? {} : { arguments: interaction.arguments }),
      };
    case "workflow-gate":
      if (typeof interaction.mode !== "string") return null;
      return {
        ...common,
        kind: "workflow-gate",
        mode: interaction.mode,
        ...(typeof interaction.inputPreview === "string"
          ? { inputPreview: interaction.inputPreview }
          : {}),
      };
    case "user-input": {
      const inputKind = readRequiredString(interaction.inputKind);
      if (!inputKind || !("payload" in interaction)) return null;
      const questions =
        inputKind === "questions" ? parseHumanInteractionQuestions(interaction.payload) : null;
      const modeChange =
        inputKind === "mode-change" ? parseHumanInteractionModeChange(interaction.payload) : null;
      return {
        ...common,
        kind: "user-input",
        inputKind,
        payload: interaction.payload,
        ...(questions ? { questions } : {}),
        ...(modeChange ? { modeChange } : {}),
      };
    }
    default:
      return null;
  }
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
  target: Pick<PendingInteraction, "source" | "streamingScopeId">,
): boolean {
  if (!target.source.callId) return false;
  if (target.streamingScopeId && message.streamingScopeId !== target.streamingScopeId) return false;
  const nodeId = message.additionalProperties?.interactionNodeId;
  if (target.source.nodeId && typeof nodeId === "string" && nodeId !== target.source.nodeId)
    return false;
  return message.contents.some(
    (content) =>
      content.type === MessageContentType.FunctionCallContent &&
      content.additionalProperties?.callId === target.source.callId,
  );
}

export function hasMatchingHumanInteractionCall(
  messages: readonly AiMessage[],
  target: Pick<PendingInteraction, "source" | "streamingScopeId">,
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
    const answer = cancelled ? null : readQuestionAnswer(answers?.[question]);
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

function readQuestionAnswer(value: unknown): string | null {
  const single = readRequiredString(value);
  if (single) return single;
  if (!Array.isArray(value) || value.length === 0) return null;
  const values = value.map(readRequiredString);
  return values.every((item): item is string => item !== null) ? values.join(", ") : null;
}
