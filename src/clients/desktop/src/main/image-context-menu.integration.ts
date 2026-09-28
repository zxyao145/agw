import assert from "node:assert/strict";
import { createServer } from "node:http";
import { mkdir, readFile } from "node:fs/promises";
import { join, resolve } from "node:path";
import { once } from "node:events";
import { setTimeout as delay } from "node:timers/promises";
import {
  app,
  BrowserWindow,
  clipboard,
  ClipboardItem,
  ipcMain,
  nativeImage,
  type Menu,
} from "electron";
import {
  imageSaveOptions,
  saveImage,
  showImageContextMenu,
  validateImageRequest,
} from "./image-context-menu";
import type { ImageContextMenuRequest } from "../shared/contracts";

const directory = resolve(".scratch/image-tests");
app.setPath("userData", join(directory, "user-data"));
app.setName("Agw image integration tests");

async function run() {
  await mkdir(directory, { recursive: true });
  await app.whenReady();
  const originalClipboard = await Promise.all(
    (await clipboard.read()).map(
      async (item) =>
        new ClipboardItem(
          Object.fromEntries(
            await Promise.all(item.types.map(async (type) => [type, await item.getType(type)])),
          ),
        ),
    ),
  );
  const source = nativeImage.createFromBitmap(Buffer.alloc(16 * 24 * 4, 128), {
    width: 16,
    height: 24,
  });
  const fixtures = [
    { extension: "png", mime: "image/png", bytes: source.toPNG() },
    { extension: "jpg", mime: "image/jpeg", bytes: source.toJPEG(90) },
    {
      extension: "gif",
      mime: "image/gif",
      bytes: Buffer.from(
        "R0lGODlhEAAYAIAAAExpcTSKwCH5BAUAAAAALAAAAAAQABgAAAIRjI+py+0Po5y02ouz3rz7rxQAOw==",
        "base64",
      ),
    },
    {
      extension: "webp",
      mime: "image/webp",
      bytes: Buffer.from(
        "UklGRmgAAABXRUJQVlA4WAoAAAAQAAAADwAAFwAAQUxQSAoAAAABB1DAiAhERP8DVlA4IDgAAAAwAwCdASoQABgAPm0skUWkIqGYBABABsSgC7LoB+AACEUAAP7aJv/xDU44+q7/8wSN1X749o4AAA==",
        "base64",
      ),
    },
  ];
  const server = createServer((request, response) => {
    const fixture = fixtures.find((item) => request.url === `/photo.${item.extension}`);
    if (!fixture) {
      response.writeHead(404, { "content-type": "text/plain" });
      response.end("Image not found");
      return;
    }
    response.writeHead(200, { "content-type": fixture.mime });
    response.end(fixture.bytes);
  });
  server.listen(0, "127.0.0.1");
  await once(server, "listening");
  const address = server.address();
  assert.ok(address && typeof address === "object");
  const origin = `http://127.0.0.1:${address.port}`;
  const window = new BrowserWindow({
    width: 640,
    height: 480,
    webPreferences: {
      preload: join(directory, "preload.cjs"),
      contextIsolation: true,
      sandbox: true,
    },
  });
  let menu: Menu | undefined;
  let menuRequest: ImageContextMenuRequest | undefined;
  const errors: unknown[] = [];
  ipcMain.handle("agw:image-context-menu", (_event, request: ImageContextMenuRequest) => {
    menuRequest = request;
    menu = showImageContextMenu(window.webContents, request, (_title, error) => errors.push(error));
  });
  try {
    await window.loadURL(
      `data:text/html,${encodeURIComponent('<html><body><img id="photo" style="width:160px;height:240px;object-fit:contain"><div contenteditable="true" id="paste"></div></body></html>')}`,
    );
    const request: ImageContextMenuRequest = {
      src: `${origin}/photo.png`,
      name: "photo.png",
      x: 80,
      y: 120,
      canCopy: true,
    };
    await window.webContents.executeJavaScript(
      `new Promise((resolve, reject) => { const image = document.querySelector("img"); image.onload = () => resolve(true); image.onerror = reject; image.src = ${JSON.stringify(request.src)}; })`,
    );
    await window.webContents.executeJavaScript(
      `window.agwDesktop.showImageContextMenu(${JSON.stringify(request)})`,
    );
    assert.deepEqual(menuRequest, request);
    assert.ok(menu);
    assert.deepEqual(
      menu.items.map((item) => item.label),
      ["Copy image", "Save image as…"],
    );
    menu.closePopup(window);
    clipboard.clear();
    menu.items[0].click();
    for (let index = 0; index < 100 && !(await clipboard.has("image/png")); index += 1)
      await delay(10);
    const copied = (await clipboard.read()).find((item) => item.types.includes("image/png"));
    assert.ok(copied);
    const blob = await copied.getType("image/png");
    assert.ok(blob instanceof Blob);
    const copiedImage = nativeImage.createFromBuffer(Buffer.from(await blob.arrayBuffer()));
    assert.deepEqual(copiedImage.getSize(), { width: 16, height: 24 });
    assert.deepEqual(copiedImage.toBitmap(), source.toBitmap());
    console.log("PASS preload, IPC, native image menu and clipboard pixels");

    await window.webContents.executeJavaScript(
      'document.querySelector("#paste").focus(); document.addEventListener("paste", (event) => { window.pastedImage = [...event.clipboardData.items].some((item) => item.kind === "file" && item.type === "image/png"); }, {once:true})',
    );
    window.webContents.paste();
    for (let index = 0; index < 100; index += 1) {
      if (await window.webContents.executeJavaScript("window.pastedImage === true")) break;
      await delay(10);
    }
    assert.equal(await window.webContents.executeJavaScript("window.pastedImage"), true);
    console.log("PASS paste receives an image file");

    const disabledMenu = showImageContextMenu(
      window.webContents,
      { ...request, canCopy: false },
      (_title, error) => errors.push(error),
    );
    assert.equal(disabledMenu.items[0].enabled, false);
    disabledMenu.closePopup(window);
    assert.throws(() => validateImageRequest({ ...request, src: "file:///private/image.png" }));
    assert.throws(() => validateImageRequest({ ...request, src: "data:text/html,content" }));
    assert.throws(() => validateImageRequest({ ...request, x: NaN }));
    assert.equal(
      imageSaveOptions("photo.jpeg", "image/jpeg").defaultPath.endsWith("photo.jpeg"),
      true,
    );

    for (const fixture of fixtures) {
      for (const kind of ["http", "data"] as const) {
        const src =
          kind === "http"
            ? `${origin}/photo.${fixture.extension}`
            : `data:${fixture.mime};base64,${fixture.bytes.toString("base64")}`;
        const saved = join(directory, `${kind}.${fixture.extension}`);
        const result = saveImage(window.webContents, { ...request, src, name: "original.wrong" });
        window.webContents.session.once("will-download", (_event, item) => {
          assert.ok(
            item.getSaveDialogOptions().defaultPath?.endsWith(`original.${fixture.extension}`),
          );
          item.setSavePath(saved);
        });
        assert.equal(await result, "completed");
        assert.deepEqual(await readFile(saved), fixture.bytes);
        const dimensions = await window.webContents.executeJavaScript(
          `new Promise((resolve, reject) => { const image = new Image(); image.onload = () => resolve([image.naturalWidth,image.naturalHeight]); image.onerror = reject; image.src = ${JSON.stringify(src)}; })`,
        );
        assert.deepEqual(dimensions, [16, 24]);
        console.log(`PASS ${kind} ${fixture.extension}: original bytes, extension and dimensions`);
      }
    }
    const cancelled = saveImage(window.webContents, request);
    window.webContents.session.once("will-download", (_event, item) => item.cancel());
    assert.equal(await cancelled, "cancelled");
    console.log("PASS cancellation");
    await assert.rejects(
      saveImage(window.webContents, { ...request, src: `${origin}/missing.png` }),
    );
    assert.deepEqual(errors, []);
    assert.equal(window.webContents.session.listenerCount("will-download"), 0);
    console.log("PASS download failure and listener cleanup");
    const closingWindow = new BrowserWindow({ show: false });
    const closedDownload = assert.rejects(
      saveImage(closingWindow.webContents, request),
      /The image window was closed/,
    );
    closingWindow.destroy();
    await closedDownload;
    assert.equal(window.webContents.session.listenerCount("will-download"), 0);
    console.log("PASS closing a window during download");
  } finally {
    await clipboard.write(originalClipboard);
    window.destroy();
    server.close();
  }
}

void run().then(
  () => app.exit(0),
  (error: unknown) => {
    console.error(error);
    app.exit(1);
  },
);
