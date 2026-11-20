import type { EventSourceOptionResponse } from "@agw/api";

export function matchesEventTypePattern(pattern: string, type: string): boolean {
  if (pattern === "*") return true;
  if (pattern.endsWith(".*")) return type.startsWith(pattern.slice(0, -1));
  return pattern === type;
}

export function getEventTypeOptions(
  sources: EventSourceOptionResponse[],
  selectedIds: string[],
): string[] {
  const selected = new Set(selectedIds);
  return [
    ...new Set(
      sources.filter((source) => selected.has(source.id)).flatMap((source) => source.eventTypes),
    ),
  ];
}

export function isAllowedEventType(type: string, options: readonly string[]): boolean {
  if (!type || /\s/.test(type)) return false;
  if (options.includes(type)) return true;
  if (type.includes("*")) return false;
  return options.some((pattern) => matchesEventTypePattern(pattern, type));
}
