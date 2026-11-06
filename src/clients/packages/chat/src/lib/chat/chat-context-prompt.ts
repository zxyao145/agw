import { buildFileCommentPrompt } from "./file-comment-prompt";
import type { LineComment } from "@agw/projects";

export function buildChatContextPrompt(
  input: string,
  context: Readonly<Record<string, string>>,
  comments: readonly LineComment[] = [],
): string {
  const contextBlock = Object.keys(context).length
    ? `<chat_context>\n${JSON.stringify(context, null, 2)}\n</chat_context>`
    : "";
  const prompt = [input.trim(), contextBlock].filter(Boolean).join("\n\n");
  return buildFileCommentPrompt(prompt, comments);
}

export function createChatContextPromptBuilder(
  context: Readonly<Record<string, string>>,
  comments: readonly LineComment[],
): (input: string) => string {
  const contextSnapshot = { ...context };
  const commentSnapshot = comments.map((comment) => ({ ...comment }));
  return (input) => buildChatContextPrompt(input, contextSnapshot, commentSnapshot);
}
