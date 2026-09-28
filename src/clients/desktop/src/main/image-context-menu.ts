import { basename, extname, join } from "node:path";
import { app, BrowserWindow, Menu, type DownloadItem, type WebContents } from "electron";

import type { ImageContextMenuRequest } from "../shared/contracts";

const activeDownloads = new WeakSet<WebContents>();
const imageExtensions: Record<string, string[]> = {
  "image/jpeg": ["jpg", "jpeg", "jpe"],
  "image/png": ["png"],
  "image/gif": ["gif"],
  "image/webp": ["webp"],
  "image/avif": ["avif"],
  "image/svg+xml": ["svg"],
  "image/bmp": ["bmp"],
};

export function showImageContextMenu(
  contents: WebContents,
  request: ImageContextMenuRequest,
  onError: (title: string, error: unknown) => void,
): Menu {
  validateImageRequest(request);
  const window = BrowserWindow.fromWebContents(contents);
  if (!window) throw new Error("The image window is unavailable.");
  const menu = Menu.buildFromTemplate([
    {
      label: "Copy image",
      enabled: request.canCopy,
      click: () => contents.copyImageAt(request.x, request.y),
    },
    {
      label: "Save image as…",
      enabled: !activeDownloads.has(contents),
      click: () => {
        void saveImage(contents, request).catch((error: unknown) =>
          onError("Unable to save image", error),
        );
      },
    },
  ]);
  menu.popup({ window });
  return menu;
}

export function validateImageRequest(request: ImageContextMenuRequest): void {
  if (
    !request ||
    typeof request.src !== "string" ||
    (request.name !== undefined && typeof request.name !== "string") ||
    !Number.isInteger(request.x) ||
    !Number.isInteger(request.y) ||
    request.x < 0 ||
    request.y < 0 ||
    typeof request.canCopy !== "boolean"
  ) {
    throw new Error("Invalid image menu request.");
  }
  const url = new URL(request.src);
  if (!["http:", "https:", "data:", "blob:"].includes(url.protocol)) {
    throw new Error("Unsupported image address.");
  }
  if (url.protocol === "data:" && !/^data:image\//iu.test(request.src)) {
    throw new Error("The image address must contain image data.");
  }
}

export function imageSaveOptions(name: string, mimeType: string) {
  const extensions = imageExtensions[mimeType];
  if (!extensions) throw new Error("The downloaded file has an unsupported image format.");
  const filename = basename(name.replaceAll("\\", "/")).replace(/[<>:"|?*\p{Cc}]/gu, "_");
  const extension = extname(filename);
  const stem = basename(filename, extension).replace(/[. ]+$/u, "") || "image";
  const savedName = extensions.includes(extension.slice(1).toLowerCase())
    ? filename
    : `${stem}.${extensions[0]}`;
  return {
    title: "Save image as",
    defaultPath: join(app.getPath("downloads"), savedName),
    filters: [{ name: "Image", extensions }],
  };
}

export async function saveImage(
  contents: WebContents,
  request: ImageContextMenuRequest,
): Promise<"completed" | "cancelled"> {
  validateImageRequest(request);
  if (activeDownloads.has(contents)) throw new Error("An image is already being saved.");
  const downloadSession = contents.session;
  activeDownloads.add(contents);
  const url = new URL(request.src);
  url.hash = "";
  let cleanup = () => {};
  try {
    return await new Promise<"completed" | "cancelled">((resolve, reject) => {
      let download: DownloadItem | undefined;
      const fail = (message: string) => {
        reject(new Error(message));
        download?.cancel();
      };
      const timeout = setTimeout(() => fail("The image download did not start."), 30_000);
      const onDestroyed = () => fail("The image window was closed.");
      const onDownload = (_event: Electron.Event, item: DownloadItem, sender: WebContents) => {
        if (sender !== contents || !item.getURLChain().includes(url.href)) return;
        download = item;
        clearTimeout(timeout);
        downloadSession.removeListener("will-download", onDownload);
        try {
          item.setSaveDialogOptions(
            imageSaveOptions(request.name || item.getFilename(), item.getMimeType()),
          );
        } catch (error) {
          reject(error);
          item.cancel();
          return;
        }
        item.once("done", (_doneEvent, state) => {
          if (state === "completed" || state === "cancelled") resolve(state);
          else reject(new Error("The image download failed."));
        });
        item.on("updated", (_updateEvent, state) => {
          if (state === "interrupted") fail("The image download was interrupted.");
        });
      };
      cleanup = () => {
        clearTimeout(timeout);
        downloadSession.removeListener("will-download", onDownload);
        contents.removeListener("destroyed", onDestroyed);
      };
      contents.once("destroyed", onDestroyed);
      downloadSession.on("will-download", onDownload);
      contents.downloadURL(url.href);
    });
  } finally {
    cleanup();
    activeDownloads.delete(contents);
  }
}
