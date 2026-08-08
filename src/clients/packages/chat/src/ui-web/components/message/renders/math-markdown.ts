type Fence = {
  character: "`" | "~";
  length: number;
};

// *?：非贪婪匹配，尽可能短
// (?<!...)：负向后行断言

// 匹配行内代码（`code`、``code`` 等）
const inlineCodePattern = /(`+)[\s\S]*?\1/g;
// 匹配 KaTeX/LaTeX 块级公式（\[...\]）
const displayMathPattern = /(?<!\\)\\\[([\s\S]*?)(?<!\\)\\\]/g;
// 匹配 KaTeX/LaTeX 行内公式（\(...\)）
const inlineMathPattern = /(?<!\\)\\\(([\s\S]*?)(?<!\\)\\\)/g;

export function normalizeMathDelimiters(markdown: string): string {
  let result = "";
  let textBuffer = "";
  let fence: Fence | null = null;

  for (const line of splitLines(markdown)) {
    if (fence) {
      result += line;
      if (isClosingFence(line, fence)) {
        fence = null;
      }
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

function splitLines(markdown: string): string[] {
  return markdown.match(/[^\n]*\n|[^\n]+$/g) ?? [];
}

function getOpeningFence(line: string): Fence | null {
  const match = line.match(/^ {0,3}(`{3,}|~{3,})/);
  if (!match) {
    return null;
  }

  return {
    character: match[1][0] as Fence["character"],
    length: match[1].length,
  };
}

function isClosingFence(line: string, fence: Fence): boolean {
  const content = line.replace(/\r?\n$/, "");
  const match = content.match(/^ {0,3}(`+|~+)[ \t]*$/);
  return match?.[1][0] === fence.character && match[1].length >= fence.length;
}

function normalizeOutsideInlineCode(markdown: string): string {
  let result = "";
  let cursor = 0;
  for (const match of markdown.matchAll(inlineCodePattern)) {
    const index = match.index;
    result += normalizeDelimiters(markdown.slice(cursor, index));
    result += match[0];
    cursor = index + match[0].length;
  }
  return result + normalizeDelimiters(markdown.slice(cursor));
}

function normalizeDelimiters(markdown: string): string {
  return markdown
    .replace(displayMathPattern, (_, formula: string) => `\n\n$$\n${formula.trim()}\n$$\n\n`)
    .replace(inlineMathPattern, (_, formula: string) => `$${formula}$`);
}
