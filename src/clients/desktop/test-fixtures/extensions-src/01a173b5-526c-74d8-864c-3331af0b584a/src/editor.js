// *.todo 文件的编辑器：textarea 通过 documents.openText 接入文档会话，读写、编码、未保存、保存快捷键与冲突都由会话处理。
// The *.todo editor: a textarea joined to a document session through documents.openText, which handles reading,
// writing, encodings, unsaved changes, the save shortcut and conflicts.
import { defineView } from "/__agw/sdk.js";

const textarea = document.querySelector("#todo");
const status = document.querySelector("#status");
let api = null;
let session = null;
// 每次输入加 1 的编辑版本。An edit version that goes up with every input.
let edits = 0;

textarea.addEventListener("input", () => {
  edits += 1;
  session.edited();
});

function render() {
  status.textContent = session.error ? `${session.state}: ${session.error.message}` : session.state;
  textarea.disabled = session.state === "loading" || session.state === "failed";
}

function notifyFailure(operation) {
  operation.catch((error) => api.ui.notify({ type: "error", message: error.message }));
}

defineView({
  onLoad(loadedApi, params) {
    api = loadedApi;
    session = api.documents.openText({
      file: params.file,
      adapter: {
        // textarea 的值统一为 LF；写回时会话按基线的换行符输出。The textarea value uses LF; the session writes the
        // baseline's line endings.
        load: ({ text }) => {
          textarea.value = text;
        },
        snapshot: () => textarea.value,
        serialize: (snapshot) => snapshot,
        matches: (snapshot) => textarea.value === snapshot,
        version: () => edits,
      },
    });
    session.onDidChange(render);
    render();
  },
  onShow(params) {
    notifyFailure(session.show(params.file));
  },
  onUnload() {
    session.dispose();
  },
});
