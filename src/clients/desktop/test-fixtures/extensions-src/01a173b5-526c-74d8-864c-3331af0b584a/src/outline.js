// Markdown 文件的只读 Outline：用 fs.readTextDocument 读取（自动识别编码），由 marked 解析后列出标题。
// A read-only Outline of Markdown files: reads with fs.readTextDocument (encoding detected) and lists the headings
// marked finds.
import { marked } from "marked";
import { defineView } from "/__agw/sdk.js";

const list = document.querySelector("#outline");
let api = null;
// 每次读取加 1，之前开始的读取的结果不再显示。Goes up with every read, so an earlier read's result is not shown.
let generation = 0;

async function render(file) {
  const current = ++generation;
  const { text } = await api.fs.readTextDocument(file.uri);
  if (current !== generation) return;
  const headings = [];
  marked.walkTokens(marked.lexer(text), (token) => {
    if (token.type === "heading") headings.push(token);
  });
  list.replaceChildren(
    ...headings.map((heading) => {
      const item = document.createElement("li");
      item.dataset.depth = String(heading.depth);
      item.textContent = heading.text;
      return item;
    }),
  );
}

defineView({
  onLoad(loadedApi, params) {
    api = loadedApi;
    return render(params.file);
  },
  // 每次显示都重新读取，显示其他查看器保存后的内容。Every show reads again to reflect other viewers' saves.
  onShow(params) {
    render(params.file).catch((error) => api.ui.notify({ type: "error", message: error.message }));
  },
});
