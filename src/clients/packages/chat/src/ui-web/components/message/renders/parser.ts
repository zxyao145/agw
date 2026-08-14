function tryParseJsonObject(str: string) {
  if (typeof str !== "string") {
    return {
      isJson: false,
      jsonObj: null,
    };
  }

  try {
    const jsonObj = JSON.parse(str);
    return {
      isJson: jsonObj !== null,
      jsonObj: jsonObj as Record<string, unknown>,
    };
  } catch {
    return {
      isJson: false,
      jsonObj: null,
    };
  }
}

function isRecord(input: unknown): input is Record<string, unknown> {
  return typeof input === "object" && input !== null && !Array.isArray(input);
}

/**
 * 按照 keys 逐层解析转义的 JSON 字符串。
 *
 * @param input 最外层 JSON 字符串或已经解析的对象
 * @param keys 逐层访问的 key
 */
function parseNestedJson(input: string | Record<string, unknown>, keys: string[]) {
  let jsonObj: Record<string, unknown> | null;
  if (typeof input === "string") {
    let current = tryParseJsonObject(input as string);
    if (current.isJson) {
      jsonObj = current.jsonObj!;
    } else {
      return input as string;
    }
  } else if (isRecord(input)) {
    jsonObj = input;
  } else {
    return input as string;
  }

  if (!jsonObj || keys.length < 1) {
    return JSON.stringify(input, null, 4);
  }

  const subKey = keys.shift() as string;
  if (!jsonObj[subKey]) {
    return JSON.stringify(input, null, 4);
  }
  return parseNestedJson(jsonObj[subKey] as string | Record<string, unknown>, keys);
}

export const formatContent = (mdText: string) => {
  const result = tryParseJsonObject(mdText);
  if (!result.isJson) {
    return mdText;
  }

  // 判断是否有 output
  const subKeyResult = result.jsonObj!["output"];
  // console.log("subKeyResult", subKeyResult)
  if (!subKeyResult) {
    const formatted = JSON.stringify(result.jsonObj, null, 4);
    return formatted;
  }

  return parseNestedJson(subKeyResult as Record<string, unknown>, [
    "hookSpecificOutput",
    // "additionalContext",
    "hookEventName",
  ]);
};
