// 调用 app.wasm 的 add(2, 40)，读取 context 并在存储目录中写入、读回 wasm.json，成功后显示 #result。
// Calls add(2, 40) from app.wasm, reads the context, writes and reads back wasm.json in storage, then renders #result.
import { defineView } from "/__agw/sdk.js";

function show(id, text) {
  const element = document.createElement("p");
  element.id = id;
  element.textContent = text;
  document.getElementById("status").replaceWith(element);
}

async function run(api) {
  const { instance } = await WebAssembly.instantiateStreaming(fetch(new URL("./app.wasm", import.meta.url)));
  const sum = instance.exports.add(2, 40);
  const { extension } = api.context.get();
  const uri = `${extension.storageUri}/wasm.json`;
  const written = JSON.stringify({ extensionId: extension.id, sum, nonce: crypto.randomUUID() });
  await api.fs.writeFile(uri, written);
  const read = await api.fs.readTextFile(uri);
  if (read !== written) throw new Error(`Storage round-trip mismatch: ${read}`);
  show("result", String(sum));
}

defineView({
  onLoad(api) {
    window.api = api;
    void run(api).catch((error) => {
      show("error", String(error));
      throw error;
    });
  },
});
