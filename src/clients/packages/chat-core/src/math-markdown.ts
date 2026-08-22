type Fence = { character: "`" | "~"; length: number };

const inlineCodePattern = /(`+)[\s\S]*?\1/g;
const displayMathPattern = /(?<!\\)\\\[([\s\S]*?)(?<!\\)\\\]/g;
const inlineMathPattern = /(?<!\\)\\\(([\s\S]*?)(?<!\\)\\\)/g;

export function normalizeMathDelimiters(markdown: string): string {
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
      result += normalizeOutsideInlineCode(textBuffer);
      textBuffer = "";
      result += line;
      fence = openingFence;
      continue;
    }

    textBuffer += line;
  }

  return result + normalizeOutsideInlineCode(textBuffer);
}

function getOpeningFence(line: string): Fence | null {
  const match = line.match(/^ {0,3}(`{3,}|~{3,})/);
  return match ? { character: match[1][0] as Fence["character"], length: match[1].length } : null;
}

function isClosingFence(line: string, fence: Fence): boolean {
  const match = line.replace(/\r?\n$/, "").match(/^ {0,3}(`+|~+)[ \t]*$/);
  return match?.[1][0] === fence.character && match[1].length >= fence.length;
}

function normalizeOutsideInlineCode(markdown: string): string {
  let result = "";
  let cursor = 0;
  for (const match of markdown.matchAll(inlineCodePattern)) {
    result += normalizeDelimiters(markdown.slice(cursor, match.index));
    result += match[0];
    cursor = match.index + match[0].length;
  }
  return result + normalizeDelimiters(markdown.slice(cursor));
}

function normalizeDelimiters(markdown: string): string {
  return markdown
    .replace(displayMathPattern, (_, formula: string) => `\n\n$$\n${formula.trim()}\n$$\n\n`)
    .replace(inlineMathPattern, (_, formula: string) => `$${formula}$`);
}
