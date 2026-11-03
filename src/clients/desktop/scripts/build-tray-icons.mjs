// 由 assets/tray-icon.svg 生成各平台的托盘图标：nativeImage 只解码 PNG、JPEG 与 Windows 上的 ICO，不解码 SVG。
// Renders each platform's tray icon from assets/tray-icon.svg: nativeImage decodes PNG, JPEG and, on Windows, ICO,
// but never SVG.
import { spawn } from "node:child_process";
import { readFile, writeFile } from "node:fs/promises";
import { resolve } from "node:path";
import { fileURLToPath } from "node:url";
import electron from "electron";

const ASSETS = resolve(import.meta.dirname, "..", "assets");
const BRAND_BLUE = "#436CF7";
const WINDOWS_SIZES = [16, 20, 24, 32, 40, 48, 64, 256];
const LINUX_SIZE = 64;

/** 在页面中把 SVG 栅格化为 PNG（base64）。Rasterizes the SVG to a base64 PNG inside the page. */
async function rasterize(source, { scale, size, fill, crop }) {
  const svg = new DOMParser().parseFromString(source, "image/svg+xml").documentElement;
  if (fill) for (const path of svg.querySelectorAll("path")) path.setAttribute("fill", fill);
  if (crop) {
    document.body.append(svg);
    const box = svg.getBBox();
    svg.remove();
    const side = Math.max(box.width, box.height) * 1.0625;
    const viewBox = [box.x + (box.width - side) / 2, box.y + (box.height - side) / 2, side, side];
    svg.setAttribute("viewBox", viewBox.join(" "));
  }
  const pixels = size ?? Number(svg.getAttribute("width")) * scale;
  svg.setAttribute("width", String(pixels));
  svg.setAttribute("height", String(pixels));
  const image = new Image();
  image.src = `data:image/svg+xml;charset=utf-8,${encodeURIComponent(new XMLSerializer().serializeToString(svg))}`;
  await image.decode();
  const canvas = document.createElement("canvas");
  canvas.width = canvas.height = pixels;
  canvas.getContext("2d").drawImage(image, 0, 0, pixels, pixels);
  return canvas.toDataURL("image/png").slice("data:image/png;base64,".length);
}

/** 以 PNG 图像组成 ICO。Packs PNG images into an ICO. */
function ico(images) {
  const directory = Buffer.alloc(6 + 16 * images.length);
  directory.writeUInt16LE(1, 2);
  directory.writeUInt16LE(images.length, 4);
  let offset = directory.length;
  images.forEach(({ size, png }, index) => {
    const entry = 6 + 16 * index;
    directory.writeUInt8(size % 256, entry);
    directory.writeUInt8(size % 256, entry + 1);
    directory.writeUInt16LE(1, entry + 4);
    directory.writeUInt16LE(32, entry + 6);
    directory.writeUInt32LE(png.length, entry + 8);
    directory.writeUInt32LE(offset, entry + 12);
    offset += png.length;
  });
  return Buffer.concat([directory, ...images.map(({ png }) => png)]);
}

async function build({ app, BrowserWindow }) {
  await app.whenReady();
  const window = new BrowserWindow({
    show: false,
    webPreferences: { backgroundThrottling: false },
  });
  await window.loadURL("about:blank");
  const source = await readFile(resolve(ASSETS, "tray-icon.svg"), "utf8");
  const render = async (options) =>
    Buffer.from(
      await window.webContents.executeJavaScript(
        `(${rasterize})(${JSON.stringify(source)}, ${JSON.stringify(options)})`,
      ),
      "base64",
    );

  // macOS：黑色加透明度的模板图，保留 SVG 设计的 22pt 画布。macOS: black-and-alpha template images on the SVG's
  // 22pt canvas.
  await writeFile(resolve(ASSETS, "tray-icon-Template.png"), await render({ scale: 1 }));
  await writeFile(resolve(ASSETS, "tray-icon-Template@2x.png"), await render({ scale: 2 }));
  // Windows 与 Linux 不会给图标着色，品牌蓝在深浅任务栏上都清晰。Windows and Linux do not tint tray icons, and the brand
  // blue reads on both light and dark taskbars.
  const colored = { fill: BRAND_BLUE, crop: true };
  const windowsImages = [];
  for (const size of WINDOWS_SIZES)
    windowsImages.push({ size, png: await render({ ...colored, size }) });
  await writeFile(resolve(ASSETS, "tray-icon.ico"), ico(windowsImages));
  await writeFile(resolve(ASSETS, "tray-icon.png"), await render({ ...colored, size: LINUX_SIZE }));
}

if (typeof electron === "string") {
  const env = { ...process.env };
  delete env.ELECTRON_RUN_AS_NODE;
  const child = spawn(electron, [fileURLToPath(import.meta.url)], { stdio: "inherit", env });
  child.on("exit", (code) => {
    process.exitCode = code ?? 1;
  });
} else {
  build(electron).then(
    () => electron.app.exit(0),
    (error) => {
      console.error(error);
      electron.app.exit(1);
    },
  );
}
