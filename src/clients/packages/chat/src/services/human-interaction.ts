import { MessageContentType, type AiMessage } from "@agw/api";

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

export type HumanInteractionQuestionAnnotation = {
  preview?: string;
  notes?: string;
};

export type HumanInteractionQuestionResponse = {
  answers: Record<string, string>;
  annotations?: Record<string, HumanInteractionQuestionAnnotation>;
};

export type HumanInteractionModeChange = {
  mode: "plan" | "execute";
};

export type HumanInteractionModeChangeResponse = {
  confirmed: true;
};

export type HumanInteractionQuestionResultItem = {
  question: string;
  answer: string | null;
};

export type HumanInteractionQuestionResult = {
  cancelled: boolean;
  items: HumanInteractionQuestionResultItem[];
};

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
    questions.push({
      question,
      header,
      options,
      multiSelect: value.multiSelect === true,
    });
  }

  return questions;
}

export function parseHumanInteractionModeChange(
  payload: unknown,
): HumanInteractionModeChange | null {
  if (!isRecord(payload)) return null;
  return payload.mode === "plan" || payload.mode === "execute" ? { mode: payload.mode } : null;
}

export function getHumanInteractionQuestionResult(
  messages: AiMessage[],
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
  return typeof value === "string" && value.length > 0 ? value : undefined;
}
