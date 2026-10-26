// Blazor 通过 IJSRuntime 导入本模块；index.html 也直接加载它，页面打开后立即与宿主握手。
// Blazor imports this module through IJSRuntime; index.html also loads it so the handshake starts when the page opens.
import { defineView } from "/__agw/sdk.js";

const connected = new Promise((resolve) => {
  defineView({
    onLoad(api) {
      window.api = api;
      resolve(api);
    },
  });
});

export async function getContext() {
  const { extension } = (await connected).context.get();
  return { extensionId: extension.id, storageUri: extension.storageUri };
}

export async function writeTextFile(uri, text) {
  await (await connected).fs.writeFile(uri, text);
}

export async function readTextFile(uri) {
  return (await connected).fs.readTextFile(uri);
}
