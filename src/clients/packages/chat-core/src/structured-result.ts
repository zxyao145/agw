// Keep the original JSON tokens (including large numbers); parse only to validate.
export function normalizeStructuredResult(text: string): string | null {
  try {
    const value: unknown = JSON.parse(text);
    return typeof value === "object" && value !== null ? text.trim() : null;
  } catch {
    // Older results may include the model's Markdown fence and surrounding prose.
  }

  const openingFence = /^[ \t]*(`{3,}|~{3,})[^\r\n]*\r?\n/m.exec(text);
  if (openingFence) {
    const bodyStart = openingFence.index + openingFence[0].length;
    const closingFence = new RegExp(`^[ \\t]*${openingFence[1]}[ \\t]*\\r?$`, "m").exec(
      text.slice(bodyStart),
    );
    if (!closingFence) return null;
    const bodyEnd = bodyStart + closingFence.index;
    const outside =
      text.slice(0, openingFence.index) + text.slice(bodyEnd + closingFence[0].length);
    if (/[{}\[\]]/.test(outside)) return null;
    const candidate = text.slice(bodyStart, bodyEnd).trim();
    try {
      const value: unknown = JSON.parse(candidate);
      return typeof value === "object" && value !== null ? candidate : null;
    } catch {
      return null;
    }
  }

  let json: string | null = null;
  for (let start = 0; start < text.length; start++) {
    if (text[start] === "}" || text[start] === "]") return null;
    if (text[start] !== "{" && text[start] !== "[") continue;
    if (json !== null) return null;

    let depth = 0;
    let inString = false;
    let escaped = false;
    let end = start;
    for (; end < text.length; end++) {
      const char = text[end];
      if (inString) {
        if (escaped) escaped = false;
        else if (char === "\\") escaped = true;
        else if (char === '"') inString = false;
        continue;
      }
      if (char === '"') inString = true;
      else if (char === "{" || char === "[") depth++;
      else if (char === "}" || char === "]") {
        depth--;
        if (depth === 0) break;
      }
    }

    if (depth !== 0) return null;
    const candidate = text.slice(start, end + 1);
    try {
      JSON.parse(candidate);
    } catch {
      // Never salvage a nested object from a malformed outer document.
      return null;
    }
    json = candidate;
    start = end;
  }
  return json;
}
