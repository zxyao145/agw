export function formatToolJson(value: string): string {
  const trimmed = value.trim();
  const isObject = trimmed.startsWith("{") && trimmed.endsWith("}");
  const isArray = trimmed.startsWith("[") && trimmed.endsWith("]");
  if (!isObject && !isArray) return value;

  try {
    return `\n\`\`\`json\n${JSON.stringify(JSON.parse(trimmed), null, 2)}\n\`\`\``;
  } catch {
    return value;
  }
}
