import { MessageContentType, type AiMessage } from "@agw/api";
import type { ProposedPlanPresentation } from "./types";

export const PROPOSED_PLAN_OPEN_TAG = "<proposed_plan>";
export const PROPOSED_PLAN_CLOSE_TAG = "</proposed_plan>";

export function parseMessageProposedPlan(
  message: Pick<AiMessage, "role" | "additionalProperties">,
  contentType: string,
  content: string,
): ProposedPlanPresentation | null {
  if (
    message.role !== "assistant" ||
    contentType !== MessageContentType.TextContent ||
    message.additionalProperties?.type === "result"
  ) {
    return null;
  }

  return parseProposedPlan(content);
}

type MarkdownFence = {
  marker: "`" | "~";
  length: number;
};

type RootLevelLine = {
  lineStart: number;
  nextLineStart: number;
};

// 检查 assistant TextContent 中是否有独占一行、位于 Markdown 代码块外的 proposed_plan 标签。
export function parseProposedPlan(content: string): ProposedPlanPresentation | null {
  const openingTag = findRootLevelLine(content, PROPOSED_PLAN_OPEN_TAG);
  if (!openingTag) return null;

  const bodyStart = openingTag.nextLineStart;
  const closingTag = findRootLevelLine(content, PROPOSED_PLAN_CLOSE_TAG, bodyStart);
  if (closingTag) {
    return {
      leadingMarkdown: content.slice(0, openingTag.lineStart).trim(),
      markdown: content.slice(bodyStart, closingTag.lineStart).trim(),
      trailingMarkdown: content.slice(closingTag.nextLineStart).trim(),
      isClosed: true,
    };
  }

  return {
    leadingMarkdown: content.slice(0, openingTag.lineStart).trim(),
    markdown: stripPartialClosingTag(content.slice(bodyStart)).trim(),
    trailingMarkdown: "",
    isClosed: false,
  };
}

function findRootLevelLine(
  content: string,
  expectedContent: string,
  startAt = 0,
): RootLevelLine | null {
  let fence: MarkdownFence | null = null;
  let lineStart = 0;

  while (lineStart <= content.length) {
    const newlineIndex = content.indexOf("\n", lineStart);
    const rawLineEnd = newlineIndex < 0 ? content.length : newlineIndex;
    const rawLine = content.slice(lineStart, rawLineEnd);
    const line = rawLine.endsWith("\r") ? rawLine.slice(0, -1) : rawLine;
    const nextLineStart = newlineIndex < 0 ? content.length : newlineIndex + 1;

    if (fence) {
      if (isClosingFence(line, fence)) fence = null;
    } else {
      const openingFence = parseOpeningFence(line);
      if (openingFence) {
        fence = openingFence;
      } else if (lineStart >= startAt && isExpectedRootLevelLine(line, expectedContent)) {
        return { lineStart, nextLineStart };
      }
    }

    if (newlineIndex < 0) break;
    lineStart = nextLineStart;
  }

  return null;
}

function isExpectedRootLevelLine(line: string, expectedContent: string): boolean {
  const indentationLength = line.match(/^ {0,3}/u)?.[0].length ?? 0;
  return line.slice(indentationLength).trimEnd() === expectedContent;
}

function parseOpeningFence(line: string): MarkdownFence | null {
  const match = /^ {0,3}(`{3,}|~{3,})(.*)$/u.exec(line);
  if (!match) return null;

  const run = match[1];
  const marker = run[0] as MarkdownFence["marker"];
  if (marker === "`" && match[2].includes("`")) return null;

  return { marker, length: run.length };
}

function isClosingFence(line: string, fence: MarkdownFence): boolean {
  const match = /^ {0,3}(`+|~+)[\t ]*$/u.exec(line);
  if (!match) return false;

  return match[1][0] === fence.marker && match[1].length >= fence.length;
}

// stripPartialClosingTag 用于处理流式输出过程中尚未完整生成的结束标签，避免 Card 正文短暂显示协议字符。
// 比如：
// <proposed_plan>
// 计划正文
// </proposed_
// 完整的 </proposed_plan> 还没出现，函数会识别末尾的 </proposed_ 是结束标签的前半部分，并暂时移除。
// 后续 plan> 到达并合并后，解析器会识别完整结束标签，并将 isClosed 设置为 true。
function stripPartialClosingTag(content: string): string {
  const finalLineStart = content.lastIndexOf("\n") + 1;
  for (let length = PROPOSED_PLAN_CLOSE_TAG.length - 1; length > 0; length -= 1) {
    const partialTag = PROPOSED_PLAN_CLOSE_TAG.slice(0, length);
    const partialLine = findRootLevelLine(content, partialTag, finalLineStart);
    if (partialLine) {
      return content.slice(0, partialLine.lineStart);
    }
  }

  return content;
}
