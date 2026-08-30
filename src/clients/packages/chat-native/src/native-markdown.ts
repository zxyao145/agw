type Fence = { character: "`" | "~"; length: number };

const inlineCodePattern = /(`+)([\s\S]*?)\1/g;
const inlineCodePadding = "\u202f";

export function padInlineCode(markdown: string): string {
  let result = "";
  let textBuffer = "";
  let fence: Fence | null = null;

  for (const line of markdown.match(/[^\n]*\n|[^\n]+$/g) ?? []) {
    if (fence) {
      result += line;
      if (isClosingFence(line, fence)) fence = null;
      continue;
    }

    const openingFence = getOpeningFence(line);
    if (openingFence) {
      result += padInlineCodeOutsideFences(textBuffer);
      textBuffer = "";
      result += line;
      fence = openingFence;
      continue;
    }

    textBuffer += line;
  }

  return result + padInlineCodeOutsideFences(textBuffer);
}

function padInlineCodeOutsideFences(markdown: string): string {
  return markdown.replace(inlineCodePattern, (codeSpan, delimiter: string, content: string) => {
    if (!content) return codeSpan;
    const leftPadding = content.startsWith(inlineCodePadding) ? "" : inlineCodePadding;
    const rightPadding = content.endsWith(inlineCodePadding) ? "" : inlineCodePadding;
    return `${delimiter}${leftPadding}${content}${rightPadding}${delimiter}`;
  });
}

function getOpeningFence(line: string): Fence | null {
  const match = line.match(/^ {0,3}(`{3,}|~{3,})/);
  return match ? { character: match[1][0] as Fence["character"], length: match[1].length } : null;
}

function isClosingFence(line: string, fence: Fence): boolean {
  const match = line.replace(/\r?\n$/, "").match(/^ {0,3}(`+|~+)[ \t]*$/);
  return match?.[1][0] === fence.character && match[1].length >= fence.length;
}
