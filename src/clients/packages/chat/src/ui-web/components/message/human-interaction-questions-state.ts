import type {
  HumanInteractionQuestion,
  HumanInteractionQuestionResponse,
} from "../../../services/human-interaction";

export const OTHER_OPTION_VALUE = "__agw_other_option__";

export type HumanInteractionQuestionSelection = {
  selected: string[];
  otherSelected: boolean;
  otherText: string;
};

export type HumanInteractionQuestionSelections = Record<string, HumanInteractionQuestionSelection>;

export function createQuestionSelections(
  questions: HumanInteractionQuestion[],
): HumanInteractionQuestionSelections {
  return Object.fromEntries(
    questions.map((question) => [
      question.question,
      { selected: [], otherSelected: false, otherText: "" },
    ]),
  );
}

export function buildQuestionResponse(
  questions: HumanInteractionQuestion[],
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
