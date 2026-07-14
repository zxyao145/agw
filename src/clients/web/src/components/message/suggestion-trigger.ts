export type SuggestionTrigger = {
  type: "command" | "file";
  query: string;
  start: number;
};

export function getTrailingSuggestionTrigger(input: string): SuggestionTrigger | null {
  const match = /(^|\s)([/@])([^\s]*)$/.exec(input);
  if (!match || match.index === undefined) {
    return null;
  }

  const prefix = match[1];
  const marker = match[2];
  return {
    type: marker === "/" ? "command" : "file",
    query: match[3],
    start: match.index + prefix.length,
  };
}

export function replaceTrailingSuggestion(input: string, suggestionText: string): string {
  const trigger = getTrailingSuggestionTrigger(input);
  if (!trigger) {
    return input;
  }

  return `${input.slice(0, trigger.start)}${suggestionText} `;
}
