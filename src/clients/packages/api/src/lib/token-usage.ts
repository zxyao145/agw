import type { AiMessage } from "../types/message";

const USAGE_CONTENT_TYPE = "UsageContent";

export interface TokenUsage {
  inputTokenCount: number;
  outputTokenCount: number;
  totalTokenCount: number;
  cachedInputTokenCount: number;
  reasoningTokenCount: number;
}

export type TokenUsageInput = Partial<Record<keyof TokenUsage, unknown>>;

export const EMPTY_TOKEN_USAGE: TokenUsage = Object.freeze({
  inputTokenCount: 0,
  outputTokenCount: 0,
  totalTokenCount: 0,
  cachedInputTokenCount: 0,
  reasoningTokenCount: 0,
});

function normalizeTokenCount(value: unknown): number {
  const count = typeof value === "number" ? value : Number(value);
  return Number.isFinite(count) && count >= 0 ? count : 0;
}

export function normalizeTokenUsage(usage?: TokenUsageInput | null): TokenUsage {
  return {
    inputTokenCount: normalizeTokenCount(usage?.inputTokenCount),
    outputTokenCount: normalizeTokenCount(usage?.outputTokenCount),
    totalTokenCount: normalizeTokenCount(usage?.totalTokenCount),
    cachedInputTokenCount: normalizeTokenCount(usage?.cachedInputTokenCount),
    reasoningTokenCount: normalizeTokenCount(usage?.reasoningTokenCount),
  };
}

export function addTokenUsage(current: TokenUsage, increment: TokenUsage): TokenUsage {
  return {
    inputTokenCount: current.inputTokenCount + increment.inputTokenCount,
    outputTokenCount: current.outputTokenCount + increment.outputTokenCount,
    totalTokenCount: current.totalTokenCount + increment.totalTokenCount,
    cachedInputTokenCount: current.cachedInputTokenCount + increment.cachedInputTokenCount,
    reasoningTokenCount: current.reasoningTokenCount + increment.reasoningTokenCount,
  };
}

export function getMessageTokenUsage(message: AiMessage): TokenUsage | null {
  const usageContents = message.contents.filter((content) => content.type === USAGE_CONTENT_TYPE);
  if (usageContents.length === 0) {
    return null;
  }

  return usageContents.reduce(
    (usage, content) =>
      addTokenUsage(usage, normalizeTokenUsage(content.content as TokenUsageInput | null)),
    EMPTY_TOKEN_USAGE,
  );
}

export function stripUsageContents(messages: AiMessage[]): AiMessage[] {
  return messages.flatMap((message) => {
    const contents = message.contents.filter((content) => content.type !== USAGE_CONTENT_TYPE);

    if (contents.length === message.contents.length) {
      return [message];
    }

    return contents.length > 0 ? [{ ...message, contents }] : [];
  });
}

export function formatTokenCount(value: number): string {
  const count = normalizeTokenCount(value);

  if (count < 10_000) {
    return count.toLocaleString();
  }

  const unit = count >= 1_000_000 ? 1_000_000 : 1_000;
  const suffix = unit === 1_000_000 ? "M" : "K";
  const formatted = (count / unit).toLocaleString(undefined, { maximumFractionDigits: 1 });
  return `${formatted}${suffix}`;
}
