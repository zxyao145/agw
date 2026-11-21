// Excalidraw 从 `<EXCALIDRAW_ASSET_PATH>/fonts/...` 加载字体；构建把字体复制到 index.html 旁的 `fonts/`，
// 因此资源目录是 extension 自身 origin 下 index.html 所在的目录。这个模块必须在 Excalidraw 之前导入。
// Excalidraw loads fonts from `<EXCALIDRAW_ASSET_PATH>/fonts/...`; the build copies them to `fonts/` next to index.html,
// so the asset directory is the directory of index.html on the extension's own origin. Import this module before Excalidraw.
declare global {
  interface Window {
    EXCALIDRAW_ASSET_PATH?: string | string[];
  }
}

window.EXCALIDRAW_ASSET_PATH = new URL("./", document.baseURI).href;

export {};
