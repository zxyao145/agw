import { execFileSync } from "node:child_process";
import { mkdir, rm, writeFile } from "node:fs/promises";
import { resolve } from "node:path";
import { expect, type APIRequestContext } from "@playwright/test";

import { standaloneHostTest } from "./standalone-host";

export const STAGED_FILE = "export const count = 2;\n";
export const WORKING_FILE = "export const count = 3;\n";
export const GUIDE_FILE = "export const guide = true;\n";
export const NOTES_FILE = "export const note = 'extra';\n";

type FileProject = { id: string; origin: string; extraDirectoryId: string };

export const fileProjectTest = standaloneHostTest.extend<{ fileProject: FileProject }>({
  fileProject: async ({ page, standaloneHost }, use) => {
    const { root, origin } = standaloneHost;
    const workspace = resolve(root, "workspace");
    const extra = resolve(root, "extra");
    await seedFileRepositories(workspace, extra);
    const project = await createProject(page.request, origin, workspace, extra);
    await use({
      id: project.id,
      origin,
      extraDirectoryId: project.additionalDirectories[0].id,
    });
  },
});

async function seedFileRepositories(workspace: string, extra: string) {
  await seedRepository(workspace, {
    "calc.ts": "export const count = 1;\n",
    "guide.ts": "export const guide = false;\n",
    "deleted.ts": "export const deleted = true;\n",
  });
  await writeFile(resolve(workspace, "calc.ts"), STAGED_FILE);
  git(workspace, "add", "calc.ts");
  await writeFile(resolve(workspace, "calc.ts"), WORKING_FILE);
  await writeFile(resolve(workspace, "guide.ts"), GUIDE_FILE);
  await rm(resolve(workspace, "deleted.ts"));

  await seedRepository(extra, { "notes.ts": "export const note = 'initial';\n" });
  await writeFile(resolve(extra, "notes.ts"), NOTES_FILE);
}

async function seedRepository(directory: string, files: Record<string, string>) {
  await mkdir(directory, { recursive: true });
  git(directory, "init", "--quiet");
  for (const [path, text] of Object.entries(files)) {
    await writeFile(resolve(directory, path), text);
  }
  git(directory, "add", "--", ...Object.keys(files));
  git(directory, "commit", "--quiet", "-m", "fixture");
}

function git(directory: string, ...args: string[]) {
  execFileSync(
    "git",
    [
      "-c",
      "core.autocrlf=false",
      "-c",
      "commit.gpgsign=false",
      "-c",
      "user.name=Agw Tests",
      "-c",
      "user.email=tests@agw.local",
      ...args,
    ],
    { cwd: directory },
  );
}

async function createProject(
  request: APIRequestContext,
  origin: string,
  workspace: string,
  extra: string,
) {
  const antiforgery = await request.get(`${origin}/api/auth/antiforgery`);
  await expect(antiforgery).toBeOK();
  const token = (await antiforgery.json()).data.requestToken;
  const created = await request.post(`${origin}/api/projects`, {
    headers: { "X-CSRF-TOKEN": token },
    data: {
      name: "File preview test",
      workspace,
      additionalDirectories: [{ path: extra }],
    },
  });
  await expect(created).toBeOK();
  return (await created.json()).data as { id: string; additionalDirectories: { id: string }[] };
}
