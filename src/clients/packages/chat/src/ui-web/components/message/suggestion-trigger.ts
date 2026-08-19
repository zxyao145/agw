export type SuggestionTrigger = {
  type: "command" | "file";
  query: string;
  start: number;
  end: number;
};

export type SuggestionReplacement = {
  value: string;
  caretIndex: number;
};

export function getSuggestionTrigger(input: string, caretIndex: number): SuggestionTrigger | null {
  if (caretIndex < 0 || caretIndex > input.length) {
    return null;
  }

  const match = /(^|\s)([/@])([^\s]*)$/.exec(input.slice(0, caretIndex));
  if (!match || match.index === undefined) {
    return null;
  }

  const prefix = match[1];
  const marker = match[2];
  return {
    type: marker === "/" ? "command" : "file",
    query: match[3],
    start: match.index + prefix.length,
    end: caretIndex,
  };
}

export function replaceSuggestion(
  input: string,
  suggestionText: string,
  caretIndex: number,
): SuggestionReplacement {
  const trigger = getSuggestionTrigger(input, caretIndex);
  if (!trigger) {
    return { value: input, caretIndex };
  }

  const suffix = input.slice(trigger.end);
  const hasWhitespaceSeparator = /^\s/.test(suffix);
  const separator = hasWhitespaceSeparator ? "" : " ";
  const value = `${input.slice(0, trigger.start)}${suggestionText}${separator}${suffix}`;
  const nextCaretIndex =
    trigger.start + suggestionText.length + (hasWhitespaceSeparator ? 1 : separator.length);

  return { value, caretIndex: nextCaretIndex };
}
