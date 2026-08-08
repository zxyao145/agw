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

// 检查 assistant TextContent 中，是否有 proposed_plan 标签。
// 会检查首个非空白内容是否为 <proposed_plan>
export function parseProposedPlan(content: string): ProposedPlanPresentation | null {
  const firstNonWhitespace = content.search(/\S/u);
  if (
    firstNonWhitespace < 0 ||
    isIndentedCode(content, firstNonWhitespace) ||
    !content.startsWith(PROPOSED_PLAN_OPEN_TAG, firstNonWhitespace)
  ) {
    return null;
  }

  // proposed_plan 开始位置
  const bodyStart = firstNonWhitespace + PROPOSED_PLAN_OPEN_TAG.length;
  // 对应 的proposed_plan 结束位置
  const closingTagStart = content.indexOf(PROPOSED_PLAN_CLOSE_TAG, bodyStart);
  if (closingTagStart >= 0) {
    return {
      markdown: content.slice(bodyStart, closingTagStart).trim(),
      trailingMarkdown: content.slice(closingTagStart + PROPOSED_PLAN_CLOSE_TAG.length).trim(),
      isClosed: true,
    };
  }

  return {
    markdown: stripPartialClosingTag(content.slice(bodyStart)).trim(),
    trailingMarkdown: "",
    isClosed: false,
  };
}

// 用于避免把 Markdown 缩进代码块中的 <proposed_plan> 误判为真正的 plan。
// 函数会检查开始标签所在行前面的缩进：
// 4 个或更多空格：视为代码
// 包含 Tab：视为代码
// 0～3 个空格：仍允许识别为计划标签
function isIndentedCode(content: string, firstNonWhitespace: number): boolean {
  const lineStart = content.lastIndexOf("\n", firstNonWhitespace - 1) + 1;
  const indentation = content.slice(lineStart, firstNonWhitespace);

  return indentation.includes("\t") || indentation.length >= 4;
}

// stripPartialClosingTag 用于处理流式输出过程中尚未完整生成的结束标签，避免 Card 正文短暂显示协议字符。
// 比如：
// <proposed_plan>
// 计划正文
// </proposed_
// 完整的 </proposed_plan> 还没出现，函数会识别末尾的 </proposed_ 是结束标签的前半部分，并暂时移除。
// 后续 plan> 到达并合并后，解析器会识别完整结束标签，并将 isClosed 设置为 true。
function stripPartialClosingTag(content: string): string {
  for (let length = PROPOSED_PLAN_CLOSE_TAG.length - 1; length > 0; length -= 1) {
    if (content.endsWith(PROPOSED_PLAN_CLOSE_TAG.slice(0, length))) {
      return content.slice(0, -length);
    }
  }

  return content;
}
