import { mkdir, writeFile } from "node:fs/promises";
import { dirname, join } from "node:path";

export const TEST_ICON_SVG =
  '<svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 16 16"><circle cx="8" cy="8" r="6"/></svg>';

export function testManifest(
  id: string,
  overrides: Record<string, unknown> = {},
): Record<string, unknown> {
  return {
    manifestVersion: 1,
    id,
    name: `Extension ${id}`,
    version: "1.0.0",
    icon: "icon.svg",
    background: "background.js",
    contributes: {
      commands: [{ id: "run", title: "Run" }],
      ribbon: [
        {
          id: "open",
          title: "Open",
          icon: "icon.svg",
          action: { type: "openMainView", view: "app" },
        },
      ],
      mainViews: [{ id: "app", title: "App", icon: "Box", entry: "index.html" }],
    },
    permissions: ["context.project"],
    hostPermissions: ["https://api.example.com"],
    ...overrides,
  };
}

/** 写入一个 extension 目录。Writes an extension directory. */
export async function writeTestExtension(
  directory: string,
  manifest: Record<string, unknown> | string,
  files: Record<string, string | Uint8Array> = {},
): Promise<void> {
  await mkdir(directory, { recursive: true });
  await writeFile(
    join(directory, "manifest.json"),
    typeof manifest === "string" ? manifest : JSON.stringify(manifest, null, 2),
  );
  const contents: Record<string, string | Uint8Array> = {
    "icon.svg": TEST_ICON_SVG,
    "background.js": 'import { defineBackground } from "/__agw/sdk.js";\ndefineBackground({});\n',
    "index.html": '<!doctype html><script type="module" src="./main.js"></script>',
    ...files,
  };
  for (const [path, content] of Object.entries(contents)) {
    const file = join(directory, ...path.split("/"));
    await mkdir(dirname(file), { recursive: true });
    await writeFile(file, content);
  }
}
