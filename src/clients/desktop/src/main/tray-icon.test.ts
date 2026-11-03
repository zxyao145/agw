import assert from "node:assert/strict";
import { readFileSync } from "node:fs";
import { resolve } from "node:path";
import test from "node:test";

import { trayIconFileName } from "./tray-icon";

const PNG_SIGNATURE = Buffer.from([0x89, 0x50, 0x4e, 0x47, 0x0d, 0x0a, 0x1a, 0x0a]);

function asset(name: string): Buffer {
  return readFileSync(resolve("assets", name));
}

/** PNG 的像素尺寸，读自 IHDR。The pixel size of a PNG, read from IHDR. */
function pngSize(bytes: Buffer): { width: number; height: number } {
  assert.ok(bytes.subarray(0, 8).equals(PNG_SIGNATURE), "the file is not a PNG");
  assert.equal(bytes.toString("latin1", 12, 16), "IHDR");
  return { width: bytes.readUInt32BE(16), height: bytes.readUInt32BE(20) };
}

/** ICO 目录中的图像。The images in an ICO directory. */
function icoImages(bytes: Buffer): { size: number; data: Buffer }[] {
  assert.equal(bytes.readUInt16LE(0), 0, "the file is not an ICO");
  assert.equal(bytes.readUInt16LE(2), 1, "the file is not an ICO");
  return Array.from({ length: bytes.readUInt16LE(4) }, (_, index) => {
    const entry = 6 + 16 * index;
    const offset = bytes.readUInt32LE(entry + 12);
    return {
      size: bytes.readUInt8(entry) || 256,
      data: bytes.subarray(offset, offset + bytes.readUInt32LE(entry + 8)),
    };
  });
}

test("trayIconFileName uses a macOS template PNG with a 2x sibling", () => {
  const name = trayIconFileName("darwin");
  // Electron 把文件名匹配 *Template.* 的图像标为模板图，菜单栏按明暗主题着色。Electron marks images named *Template.* as
  // template images, which the menu bar tints for light and dark appearances.
  assert.match(name, /Template\.png$/);
  const { width, height } = pngSize(asset(name));
  assert.deepEqual(pngSize(asset(name.replace(/\.png$/, "@2x.png"))), {
    width: width * 2,
    height: height * 2,
  });
});

test("trayIconFileName uses a Windows ICO with the small icon sizes for 100% to 300% scaling", () => {
  const name = trayIconFileName("win32");
  assert.match(name, /\.ico$/);
  const images = icoImages(asset(name));
  for (const image of images) {
    assert.deepEqual(pngSize(image.data), { width: image.size, height: image.size });
  }
  for (const size of [16, 20, 24, 32, 40, 48]) {
    assert.ok(
      images.some((image) => image.size === size),
      `the ICO has no ${size}px image`,
    );
  }
});

test("trayIconFileName uses a square PNG that covers 2x panels on Linux", () => {
  for (const platform of ["linux", "freebsd"] as const) {
    const { width, height } = pngSize(asset(trayIconFileName(platform)));
    assert.equal(width, height);
    // Electron 只把最大的一张位图交给面板，由面板缩小。Electron hands the panel only its largest bitmap to scale down.
    assert.ok(width >= 48, `${width}px is too small for a 2x panel`);
  }
});
