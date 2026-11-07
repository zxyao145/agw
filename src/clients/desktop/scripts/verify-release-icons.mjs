import assert from "node:assert/strict";
import { execFile } from "node:child_process";
import { createHash } from "node:crypto";
import { mkdir, mkdtemp, readFile, rm, rmdir } from "node:fs/promises";
import { extname, resolve } from "node:path";
import { buffer } from "node:stream/consumers";
import { promisify } from "node:util";
import { DOMParser } from "@xmldom/xmldom";
import { parse } from "ini";
import { Data, NtExecutable, NtExecutableResource, Resource } from "resedit";
import yauzl from "yauzl";
import forgeConfig from "../forge.config.cjs";

const execute = promisify(execFile);
const desktopDirectory = resolve(import.meta.dirname, "..");
const assetsDirectory = resolve(desktopDirectory, "assets");
const scratchDirectory = resolve(desktopDirectory, ".scratch", "release-icons");
const iconUrl = forgeConfig.makers.find((maker) => maker.name === "@electron-forge/maker-squirrel")
  .config.iconUrl;

function digest(bytes) {
  return createHash("sha256").update(bytes).digest("hex");
}

function equalIcon(actual, expected, label) {
  assert.equal(digest(actual), digest(expected), `${label} must contain the Agw icon`);
}

function windowsResources(bytes) {
  return NtExecutableResource.from(NtExecutable.from(bytes, { ignoreCert: true })).entries;
}

function verifyWindowsResources(entries, expectedIcon, label) {
  const groups = Resource.IconGroupEntry.fromEntries(entries);
  assert.ok(groups.length > 0, `${label} must contain the Agw icon`);
  const imageBytes = (image) => Buffer.from(image.isRaw() ? image.bin : image.generate());
  const actual = groups[0]
    .getIconItemsFromEntries(entries)
    .map((image) => digest(imageBytes(image)));
  const expected = Data.IconFile.from(expectedIcon).icons.map(({ data }) =>
    digest(imageBytes(data)),
  );
  assert.deepEqual(actual.sort(), expected.sort(), `${label} must contain the Agw icon`);
}

export function verifyWindowsIcon(executable, expectedIcon, label) {
  verifyWindowsResources(windowsResources(executable), expectedIcon, label);
}

function zipEntries(bytes, select) {
  return new Promise((resolvePromise, reject) => {
    yauzl.fromBuffer(bytes, { lazyEntries: true }, (error, zip) => {
      if (error) return reject(error);
      const entries = new Map();
      const fail = (failure) => {
        zip.close();
        reject(failure);
      };
      zip.on("error", fail);
      zip.on("end", () => resolvePromise(entries));
      zip.on("entry", (entry) => {
        if (!select(entry.fileName)) return zip.readEntry();
        zip.openReadStream(entry, (streamError, stream) => {
          if (streamError) return fail(streamError);
          buffer(stream).then((contents) => {
            entries.set(entry.fileName, contents);
            zip.readEntry();
          }, fail);
        });
      });
      zip.readEntry();
    });
  });
}

function oneEntry(entries, select, label) {
  const matches = [...entries].filter(([name]) => select(name));
  assert.equal(matches.length, 1, `${label} must have exactly one matching file`);
  return matches[0][1];
}

async function verifyWindowsInstaller(artifact, expectedIcon) {
  const resources = windowsResources(await readFile(artifact));
  verifyWindowsResources(resources, expectedIcon, artifact);
  // Squirrel 将安装内容存入 DATA/131 ZIP resource。Squirrel stores its installer payload in the DATA/131 ZIP resource.
  const payload = resources.find((entry) => entry.type === "DATA" && entry.id === 131);
  assert.ok(payload, `${artifact} must contain its Squirrel payload`);
  const entries = await zipEntries(
    Buffer.from(payload.bin),
    (name) => ["background.gif", "setupIcon.ico"].includes(name) || name.endsWith("-full.nupkg"),
  );
  equalIcon(
    entries.get("background.gif"),
    await readFile(resolve(assetsDirectory, "agw-installer.gif")),
    "Installer splash",
  );
  equalIcon(entries.get("setupIcon.ico"), expectedIcon, "Installer payload icon");
  const packageBytes = oneEntry(
    entries,
    (name) => name.endsWith("-full.nupkg"),
    "Squirrel payload",
  );
  const packageEntries = await zipEntries(
    packageBytes,
    (name) =>
      name.endsWith(".nuspec") ||
      ["lib/net45/agw-desktop.exe", "lib/net45/squirrel.exe"].includes(name),
  );
  verifyWindowsIcon(
    packageEntries.get("lib/net45/agw-desktop.exe"),
    expectedIcon,
    "Installed application",
  );
  verifyWindowsIcon(
    packageEntries.get("lib/net45/squirrel.exe"),
    expectedIcon,
    "Installed updater",
  );
  const nuspec = oneEntry(packageEntries, (name) => name.endsWith(".nuspec"), "NuGet package");
  const document = new DOMParser({
    onError: (_level, message) => {
      throw new Error(message);
    },
  }).parseFromString(nuspec.toString("utf8"), "application/xml");
  assert.equal(
    document.getElementsByTagName("iconUrl")[0]?.textContent,
    iconUrl,
    "Control Panel icon URL",
  );
}

async function verifyMacInstaller(artifact) {
  const mountPoint = await mkdtemp(resolve(scratchDirectory, "dmg-"));
  await execute("hdiutil", [
    "attach",
    "-readonly",
    "-nobrowse",
    "-noautoopen",
    "-mountpoint",
    mountPoint,
    artifact,
  ]);
  try {
    equalIcon(
      await readFile(resolve(mountPoint, ".VolumeIcon.icns")),
      await readFile(resolve(assetsDirectory, "agw-logo.icns")),
      "DMG volume",
    );
    const { stdout: customIcon } = await execute("xcrun", ["GetFileInfo", "-aC", mountPoint]);
    assert.equal(customIcon.trim(), "1", "DMG volume must enable its custom icon");
    const application = resolve(mountPoint, "Agw Desktop.app", "Contents");
    const { stdout: iconFile } = await execute("plutil", [
      "-extract",
      "CFBundleIconFile",
      "raw",
      "-o",
      "-",
      resolve(application, "Info.plist"),
    ]);
    equalIcon(
      await readFile(resolve(application, "Resources", iconFile.trim())),
      await readFile(resolve(assetsDirectory, "agw-logo.icns")),
      "macOS application",
    );
    await execute("codesign", [
      "--verify",
      "--deep",
      "--strict",
      resolve(mountPoint, "Agw Desktop.app"),
    ]);
  } finally {
    await execute("hdiutil", ["detach", mountPoint]);
    await rmdir(mountPoint);
  }
}

async function verifyLinuxInstaller(artifact) {
  const directory = await mkdtemp(resolve(scratchDirectory, "deb-"));
  try {
    await execute("dpkg-deb", ["--extract", artifact, directory]);
    const desktopFile = resolve(directory, "usr", "share", "applications", "agw-desktop.desktop");
    await execute("desktop-file-validate", [desktopFile]);
    const entry = parse(await readFile(desktopFile, "utf8"))["Desktop Entry"];
    assert.equal(entry.Icon, "agw-desktop", "Linux launcher must reference its installed Agw icon");
    const expected = await readFile(resolve(assetsDirectory, "agw-logo.png"));
    equalIcon(
      await readFile(resolve(directory, "usr", "share", "pixmaps", `${entry.Icon}.png`)),
      expected,
      "Linux launcher",
    );
    equalIcon(
      await readFile(
        resolve(directory, "usr", "lib", "agw-desktop", "resources", "assets", "agw-logo.png"),
      ),
      expected,
      "Linux window",
    );
  } finally {
    await rm(directory, { recursive: true, force: true });
  }
}

export async function verifyReleaseIcons({ platform, applicationDirectory, artifacts }) {
  await mkdir(scratchDirectory, { recursive: true });
  const resources =
    platform === "darwin"
      ? resolve(applicationDirectory, "Agw Desktop.app", "Contents", "Resources")
      : resolve(applicationDirectory, "resources");
  equalIcon(
    await readFile(resolve(resources, "assets", "agw-logo.png")),
    await readFile(resolve(assetsDirectory, "agw-logo.png")),
    "Packaged window",
  );
  const expectedIcon = await readFile(resolve(assetsDirectory, "agw-logo.ico"));
  if (platform === "win32") {
    verifyWindowsIcon(
      await readFile(resolve(applicationDirectory, "agw-desktop.exe")),
      expectedIcon,
      "Packaged application",
    );
  }
  for (const artifact of artifacts) {
    if (platform === "darwin") await verifyMacInstaller(artifact);
    else if (platform === "linux") await verifyLinuxInstaller(artifact);
    else if (extname(artifact) === ".exe") await verifyWindowsInstaller(artifact, expectedIcon);
    else {
      const entries = await zipEntries(
        await readFile(artifact),
        (name) => name === "agw-desktop.exe" || name.endsWith("/agw-desktop.exe"),
      );
      verifyWindowsIcon(
        oneEntry(entries, () => true, "Portable ZIP"),
        expectedIcon,
        "Portable application",
      );
    }
    console.log(`Verified Agw icons: ${artifact}`);
  }
}
