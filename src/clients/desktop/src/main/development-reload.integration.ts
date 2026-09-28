import { app, nativeImage } from "electron";
import { writeFileSync, renameSync } from "node:fs";
import { join, resolve } from "node:path";

const directory = resolve(".scratch/development-reload");
app.setPath("userData", join(directory, "user-data"));
app.on("browser-window-created", (_event, window) => {
  window.webContents.once("did-finish-load", () => {
    void (async () => {
      const image = nativeImage.createFromBitmap(Buffer.alloc(4, 128), { width: 1, height: 1 });
      await window.webContents.executeJavaScript(
        `window.agwDesktop.showImageContextMenu(${JSON.stringify({
          src: image.toDataURL(),
          x: 0,
          y: 0,
          canCopy: false,
        })})`,
      );
      writeFileSync(join(directory, "ready.pending.json"), JSON.stringify({ pid: process.pid }));
      renameSync(join(directory, "ready.pending.json"), join(directory, "ready.json"));
    })().catch((error: unknown) => {
      console.error(error);
      app.exit(1);
    });
  });
});

require(resolve("dist/main/index.js"));
app.setName("Agw development reload tests");
