import assert from "node:assert/strict";
import test from "node:test";

import { detectLanguage } from "./languages";

// 固定的语言列表：前几项取自 Monaco 0.57.0 的注册信息；`.d.ts` 与 `makefile` 两项用来验证最长扩展名与不区分大小写的文件名。
// A fixed language list: the first entries come from Monaco 0.57.0's registrations; the `.d.ts` and `makefile`
// entries check the longest extension and case-insensitive file names.
const LANGUAGES = [
  { id: "plaintext", extensions: [".txt"], aliases: ["Plain Text", "text"] },
  { id: "typescript", extensions: [".ts", ".tsx", ".cts", ".mts"], aliases: ["TypeScript"] },
  {
    id: "json",
    extensions: [".json", ".bowerrc", ".jshintrc", ".jscsrc", ".eslintrc", ".babelrc", ".har"],
    aliases: ["JSON", "json"],
  },
  { id: "yaml", extensions: [".yaml", ".yml"], aliases: ["YAML"] },
  { id: "dockerfile", extensions: [".dockerfile"], filenames: ["Dockerfile"] },
  { id: "typescript-declaration", extensions: [".d.ts"] },
  { id: "makefile", filenames: ["makefile"] },
];

test("detectLanguage matches a file name first, ignoring case", () => {
  assert.equal(detectLanguage("Dockerfile", LANGUAGES), "dockerfile");
  assert.equal(detectLanguage("dockerfile", LANGUAGES), "dockerfile");
  assert.equal(detectLanguage("Makefile", LANGUAGES), "makefile");
});

test("detectLanguage takes the longest matching extension", () => {
  assert.equal(detectLanguage("a.d.ts", LANGUAGES), "typescript-declaration");
  assert.equal(detectLanguage("a.tsx", LANGUAGES), "typescript");
  assert.equal(detectLanguage("a.json", LANGUAGES), "json");
  assert.equal(detectLanguage("a.yml", LANGUAGES), "yaml");
  assert.equal(detectLanguage("B.YML", LANGUAGES), "yaml");
});

test("detectLanguage falls to plaintext when nothing matches", () => {
  assert.equal(detectLanguage("LICENSE", LANGUAGES), "plaintext");
  assert.equal(detectLanguage("a.py", LANGUAGES), "plaintext");
});
