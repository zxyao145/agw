import type { Page } from "@playwright/test";

/** Source 显示的 TypeScript 文件。The TypeScript file Source shows. */
export const SOURCE_TEXT = [
  "export function greet(name: string) {",
  "  return `Hello, ${name}`;",
  "}",
  "",
].join("\n");

/** Only diff 中工作区相对暂存区的改动。The working-tree change against the index in Only diff. */
export const DIFF_ORIGINAL = ["const a = 1;", "const b = 2;", "const c = 3;", ""].join("\n");
export const DIFF_MODIFIED = [
  "const a = 1;",
  "const b = 20;",
  "const c = 3;",
  "const d = 4;",
  "",
].join("\n");
const UNIFIED_DIFF = [
  "diff --git a/calc.ts b/calc.ts",
  "--- a/calc.ts",
  "+++ b/calc.ts",
  "@@ -1,3 +1,4 @@",
  " const a = 1;",
  "-const b = 2;",
  "+const b = 20;",
  " const c = 3;",
  "+const d = 4;",
  "",
].join("\n");

const FILES: Record<string, string> = {
  "app.ts": SOURCE_TEXT,
  "calc.ts": DIFF_MODIFIED,
};

const FILE_ITEMS = [
  { name: "app.ts", path: "app.ts", type: "file", size: SOURCE_TEXT.length },
  { name: "calc.ts", path: "calc.ts", type: "file", size: DIFF_MODIFIED.length },
];

const CHANGED_ITEMS = [
  {
    name: "calc.ts",
    path: "calc.ts",
    type: "file",
    gitStatus: "modified",
    gitStagedStatus: null,
    gitUnstagedStatus: "modified",
  },
];

/** 在 `mockChatApi` 之后调用，`/api/files/*` 由这里回答。Call after `mockChatApi`; it answers `/api/files/*`. */
export async function mockFilesApi(page: Page) {
  await page.route("**/api/files/**", async (route) => {
    const request = route.request();
    const url = new URL(request.url());
    if (request.method() !== "GET") {
      throw new Error(`Unexpected mutation: ${request.method()} ${url.pathname}`);
    }
    const path = url.searchParams.get("path") ?? "";
    let data: unknown;
    if (url.pathname === "/api/files/list") {
      data = { items: url.searchParams.get("diff") === "true" ? CHANGED_ITEMS : FILE_ITEMS };
    } else if (url.pathname === "/api/files/read" && path in FILES) {
      data = FILES[path];
    } else if (
      url.pathname === "/api/files/diff" &&
      path === "calc.ts" &&
      url.searchParams.get("scope") === "unstaged"
    ) {
      data = {
        diff: UNIFIED_DIFF,
        unchanged: false,
        isBinary: false,
        originalContent: DIFF_ORIGINAL,
        modifiedContent: DIFF_MODIFIED,
      };
    } else {
      throw new Error(`Unexpected files query: ${url.pathname}${url.search}`);
    }
    await route.fulfill({ json: { code: 0, title: "OK", data } });
  });
}
