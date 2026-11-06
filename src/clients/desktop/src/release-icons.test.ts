import assert from "node:assert/strict";
import { readFile } from "node:fs/promises";
import { dirname, resolve } from "node:path";
import test from "node:test";
import { Data } from "resedit";
import sharp from "sharp";

test("WindowsIcon_AgwResource_ContainsDecodableApplicationImage", async () => {
  const icon = Data.IconFile.from(await readFile(resolve("assets", "agw-logo.ico")));

  assert.ok(icon.icons.length > 0);
  for (const { data } of icon.icons) {
    assert.ok(data.isRaw());
    const metadata = await sharp(Buffer.from(data.bin)).metadata();
    assert.equal(metadata.format, "png");
    assert.equal(metadata.width, 256);
    assert.equal(metadata.height, 256);
  }
});

test("InstallerGif_AgwResource_IsDecodableAtInstallerSize", async () => {
  const metadata = await sharp(resolve("assets", "agw-installer.gif")).metadata();

  assert.equal(metadata.format, "gif");
  assert.equal(metadata.width, 256);
  assert.equal(metadata.height, 256);
});

test("VerifyWindowsIcon_DefaultInstaller_RejectsBrandMismatch", async () => {
  const { verifyWindowsIcon } = await import("../scripts/verify-release-icons.mjs");
  const installer = resolve(
    dirname(require.resolve("electron-winstaller")),
    "..",
    "vendor",
    "Setup.exe",
  );
  const executable = await readFile(installer);
  const icon = await readFile(resolve("assets", "agw-logo.ico"));

  assert.throws(() => verifyWindowsIcon(executable, icon, "Setup.exe"), /Setup\.exe.*Agw icon/u);
});
