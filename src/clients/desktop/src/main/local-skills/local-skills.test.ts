import assert from "node:assert/strict";
import { mkdir, readFile, rename, rm, symlink, utimes, writeFile } from "node:fs/promises";
import { dirname, join } from "node:path";
import test from "node:test";
import yauzl from "yauzl";

import { createScratchDirectory } from "../scratch-directory.test-helper";
import { packLocalSkill, scanLocalSkills } from "./local-skills";

const goldenDigest = "sha256:fd5db01f805c3fa8eaec5b16afe3d3bc9c02ebf1b9e7081ade4674d50ba3384e";
const demoSkill = {
  "SKILL.md": "---\nname: demo\ndescription: Demo skill.\n---\nUse it.\n",
  "scripts/run.py": "print('hi')\n",
};

async function withHome(action: (home: string) => Promise<void>): Promise<void> {
  const scratch = await createScratchDirectory("local-skills");
  try {
    await action(scratch.path);
  } finally {
    await scratch.dispose();
  }
}

async function skillsDirectory(home: string): Promise<string> {
  const directory = join(home, ".agents", "skills");
  await mkdir(directory, { recursive: true });
  return directory;
}

async function writeSkill(
  home: string,
  directoryName: string,
  files: Record<string, string>,
): Promise<string> {
  const root = join(await skillsDirectory(home), directoryName);
  for (const [relativePath, contents] of Object.entries(files)) {
    const path = join(root, ...relativePath.split("/"));
    await mkdir(dirname(path), { recursive: true });
    await writeFile(path, contents, "utf8");
  }
  return root;
}

async function readZip(bytes: Uint8Array): Promise<Map<string, Buffer>> {
  return await new Promise((resolve, reject) => {
    yauzl.fromBuffer(Buffer.from(bytes), { lazyEntries: true }, (error, zipFile) => {
      if (error) {
        reject(error);
        return;
      }
      if (!zipFile) {
        reject(new Error("ZIP file was not opened."));
        return;
      }

      const files = new Map<string, Buffer>();
      zipFile.on("entry", (entry) => {
        zipFile.openReadStream(entry, (streamError, stream) => {
          if (streamError) {
            reject(streamError);
            return;
          }
          if (!stream) {
            reject(new Error(`ZIP entry ${entry.fileName} has no read stream.`));
            return;
          }

          const chunks: Buffer[] = [];
          stream.on("data", (chunk: Buffer) => chunks.push(chunk));
          stream.on("error", reject);
          stream.on("end", () => {
            files.set(entry.fileName, Buffer.concat(chunks));
            zipFile.readEntry();
          });
        });
      });
      zipFile.on("error", reject);
      zipFile.on("end", () => resolve(files));
      zipFile.readEntry();
    });
  });
}

test("scanLocalSkills_DirectoryMissing_ReturnsNotExists", async () => {
  await withHome(async (home) => {
    assert.deepEqual(await scanLocalSkills(home), {
      directory: join(home, ".agents", "skills"),
      exists: false,
      skills: [],
    });
  });
});

test("scanLocalSkills_PathIsFile_Throws", async () => {
  await withHome(async (home) => {
    const directory = join(home, ".agents", "skills");
    await mkdir(dirname(directory), { recursive: true });
    await writeFile(directory, "file");
    await assert.rejects(scanLocalSkills(home), new Error(`${directory} isn't a directory.`));
  });
});

test("scanLocalSkills_ValidSkill_ReturnsMetadata", async () => {
  await withHome(async (home) => {
    await writeSkill(home, "demo", demoSkill);
    const scan = await scanLocalSkills(home);
    assert.equal(scan.exists, true);
    assert.deepEqual(scan.skills, [
      {
        directoryName: "demo",
        name: "demo",
        description: "Demo skill.",
        sourceDigest: goldenDigest,
        fileCount: 2,
        totalBytes:
          Buffer.byteLength(demoSkill["SKILL.md"]) + Buffer.byteLength(demoSkill["scripts/run.py"]),
        problem: null,
      },
    ]);
  });
});

test("scanLocalSkills_HiddenEntriesAndFiles_Skipped", async () => {
  await withHome(async (home) => {
    const skills = await skillsDirectory(home);
    await mkdir(join(skills, ".system"), { recursive: true });
    await writeFile(join(skills, "README.md"), "ignored");
    await writeSkill(home, "demo", demoSkill);
    await writeFile(join(skills, "demo", ".DS_Store"), "ignored");
    await mkdir(join(skills, "demo", ".git"));
    await writeFile(join(skills, "demo", ".git", "HEAD"), "ignored");

    const scan = await scanLocalSkills(home);
    assert.deepEqual(
      scan.skills.map((skill) => skill.directoryName),
      ["demo"],
    );
    assert.equal(scan.skills[0].fileCount, 2);
    assert.equal(scan.skills[0].sourceDigest, goldenDigest);
  });
});

test("scanLocalSkills_TopLevelSymlink_ScansTarget", async () => {
  await withHome(async (home) => {
    const target = await writeSkill(home, "source", demoSkill);
    const externalTarget = join(home, "external-skill");
    await rename(target, externalTarget);
    const skills = await skillsDirectory(home);
    await symlink(externalTarget, join(skills, "linked"), "junction");

    const scan = await scanLocalSkills(home);
    assert.deepEqual(
      scan.skills.map((skill) => skill.directoryName),
      ["linked"],
    );
    assert.equal(scan.skills[0].sourceDigest, goldenDigest);
  });
});

test("scanLocalSkills_BrokenTopLevelSymlink_ReportsProblem", async () => {
  await withHome(async (home) => {
    const skills = await skillsDirectory(home);
    await symlink(join(home, "missing"), join(skills, "broken"), "junction");
    const scan = await scanLocalSkills(home);
    assert.equal(scan.skills[0].directoryName, "broken");
    assert.equal(scan.skills[0].problem, "Broken symbolic link.");
    assert.equal(scan.skills[0].sourceDigest, null);
  });
});

test("scanLocalSkills_MissingOrInvalidFrontMatter_ReportsProblem", async () => {
  await withHome(async (home) => {
    await writeSkill(home, "missing", { "note.md": "missing" });
    await writeSkill(home, "plain", { "SKILL.md": "Use it.\n" });
    await writeSkill(home, "yaml", { "SKILL.md": "---\nname: [\n---\nUse it.\n" });
    await writeSkill(home, "no-name", {
      "SKILL.md": "---\ndescription: Demo skill.\n---\nUse it.\n",
    });
    await writeSkill(home, "no-description", { "SKILL.md": "---\nname: demo\n---\nUse it.\n" });

    const scan = await scanLocalSkills(home);
    const byDirectory = new Map(scan.skills.map((skill) => [skill.directoryName, skill]));
    assert.equal(byDirectory.get("missing")?.problem, "SKILL.md is missing.");
    assert.equal(byDirectory.get("plain")?.problem, "SKILL.md has no front matter.");
    assert.match(byDirectory.get("yaml")?.problem ?? "", /^Front matter isn't valid YAML: /u);
    assert.equal(byDirectory.get("no-name")?.problem, "Front matter needs a name.");
    assert.equal(byDirectory.get("no-description")?.problem, "Front matter needs a description.");
  });
});

test("scanLocalSkills_SymlinkOutsideSkill_ReportsProblem", async () => {
  await withHome(async (home) => {
    const root = await writeSkill(home, "demo", demoSkill);
    const outside = join(home, "outside.txt");
    await writeFile(outside, "outside");
    await symlink(outside, join(root, "outside.txt"));

    const skill = (await scanLocalSkills(home)).skills[0];
    assert.equal(skill.problem, 'Symbolic link "outside.txt" points outside the skill directory.');
    assert.equal(skill.sourceDigest, null);
  });
});

test("scanLocalSkills_SymlinkToDirectory_ReportsProblem", async () => {
  await withHome(async (home) => {
    const root = await writeSkill(home, "demo", demoSkill);
    await mkdir(join(root, "folder"));
    await symlink(join(root, "folder"), join(root, "linked-folder"), "junction");

    const skill = (await scanLocalSkills(home)).skills[0];
    assert.equal(skill.problem, 'Symbolic link "linked-folder" points to a directory.');
  });
});

test("scanLocalSkills_SymlinkInsideSkill_UsesTargetContent", async () => {
  await withHome(async (home) => {
    const linkedRoot = await writeSkill(home, "demo", {
      ...demoSkill,
      "scripts/source.py": "print('source')\n",
    });
    await symlink("source.py", join(linkedRoot, "scripts", "alias.py"));
    const linkedScan = await scanLocalSkills(home);
    const linkedSkill = linkedScan.skills[0];
    await rm(linkedRoot, { recursive: true });
    const copiedRoot = await writeSkill(home, "demo", {
      ...demoSkill,
      "scripts/source.py": "print('source')\n",
      "scripts/alias.py": "print('source')\n",
    });

    const copiedSkill = (await scanLocalSkills(home)).skills[0];
    assert.equal(linkedSkill.problem, null);
    assert.equal(linkedSkill.sourceDigest, copiedSkill.sourceDigest);
    assert.equal(linkedSkill.fileCount, 4);
    assert.equal(copiedSkill.fileCount, 4);
    assert.equal(
      await readFile(join(copiedRoot, "scripts", "alias.py"), "utf8"),
      "print('source')\n",
    );
  });
});

test("scanLocalSkills_DuplicateNames_ReportsProblemOnBoth", async () => {
  await withHome(async (home) => {
    const sameName = { "SKILL.md": "---\nname: shared\ndescription: Shared.\n---\n" };
    await writeSkill(home, "first", sameName);
    await writeSkill(home, "second", sameName);
    const scan = await scanLocalSkills(home);
    assert.deepEqual(
      scan.skills.map((skill) => skill.problem),
      ['Another directory uses the name "shared".', 'Another directory uses the name "shared".'],
    );
    assert.deepEqual(
      scan.skills.map((skill) => skill.sourceDigest),
      [null, null],
    );
  });
});

test("sourceDigest_SameContentDifferentTimes_IsEqual", async () => {
  const first = await createScratchDirectory("local-skills");
  const second = await createScratchDirectory("local-skills");
  try {
    const firstRoot = await writeSkill(first.path, "demo", demoSkill);
    const secondRoot = await writeSkill(second.path, "demo", demoSkill);
    await utimes(
      join(firstRoot, "SKILL.md"),
      new Date("2000-01-01T00:00:00Z"),
      new Date("2000-01-01T00:00:00Z"),
    );
    await utimes(
      join(secondRoot, "SKILL.md"),
      new Date("2020-01-01T00:00:00Z"),
      new Date("2020-01-01T00:00:00Z"),
    );

    const firstDigest = (await scanLocalSkills(first.path)).skills[0].sourceDigest;
    const secondDigest = (await scanLocalSkills(second.path)).skills[0].sourceDigest;
    assert.equal(firstDigest, secondDigest);
  } finally {
    await first.dispose();
    await second.dispose();
  }
});

test("sourceDigest_ContentOrPathChanged_Differs", async () => {
  await withHome(async (home) => {
    const root = await writeSkill(home, "demo", { ...demoSkill, "notes/a.txt": "one" });
    const initial = (await scanLocalSkills(home)).skills[0].sourceDigest;
    await writeFile(join(root, "notes", "a.txt"), "two");
    const changedContent = (await scanLocalSkills(home)).skills[0].sourceDigest;
    await rename(join(root, "notes", "a.txt"), join(root, "notes", "b.txt"));
    const changedPath = (await scanLocalSkills(home)).skills[0].sourceDigest;
    assert.notEqual(initial, changedContent);
    assert.notEqual(changedContent, changedPath);
  });
});

test("sourceDigest_IgnoredEntries_DoNotAffectDigest", async () => {
  await withHome(async (home) => {
    const root = await writeSkill(home, "demo", demoSkill);
    const initial = (await scanLocalSkills(home)).skills[0];
    await writeFile(join(root, ".DS_Store"), "ignored");
    await writeFile(join(root, "Thumbs.db"), "ignored");
    await writeFile(join(root, "desktop.ini"), "ignored");
    await mkdir(join(root, ".git"));
    await writeFile(join(root, ".git", "HEAD"), "ignored");
    const after = (await scanLocalSkills(home)).skills[0];
    assert.equal(after.sourceDigest, initial.sourceDigest);
    assert.equal(after.fileCount, initial.fileCount);
    assert.equal(after.totalBytes, initial.totalBytes);
  });
});

test("scanLocalSkills_FixedTree_MatchesGoldenDigest", async () => {
  await withHome(async (home) => {
    await writeSkill(home, "demo", demoSkill);
    assert.equal((await scanLocalSkills(home)).skills[0].sourceDigest, goldenDigest);
  });
});

test("scanLocalSkills_BomAndCrlfFrontMatter_ParsesMetadata", async () => {
  await withHome(async (home) => {
    await writeSkill(home, "demo", {
      "SKILL.md": "\uFEFF---\r\nname: demo\r\ndescription: Demo skill.\r\n---\r\nUse it.\r\n",
    });
    const skill = (await scanLocalSkills(home)).skills[0];
    assert.equal(skill.name, "demo");
    assert.equal(skill.description, "Demo skill.");
    assert.equal(skill.problem, null);
  });
});

test("scanLocalSkills_TopLevelSymlinkToRoot_ReportsProblemWithoutLooping", async () => {
  await withHome(async (home) => {
    const skills = await skillsDirectory(home);
    await symlink(skills, join(skills, "loop"), "junction");
    const scan = await scanLocalSkills(home);
    assert.equal(scan.skills.length, 1);
    assert.equal(scan.skills[0].directoryName, "loop");
    assert.notEqual(scan.skills[0].problem, null);
  });
});

test("packLocalSkill_ValidSkill_ZipMatchesFilesAndDigest", async () => {
  await withHome(async (home) => {
    const files = { ...demoSkill, "参考/说明.md": "UTF-8 content\n" };
    const root = await writeSkill(home, "demo", files);
    await writeFile(join(root, ".DS_Store"), "ignored");
    const scan = await scanLocalSkills(home);
    const packed = await packLocalSkill(home, "demo");
    const zipped = await readZip(packed.bytes);
    assert.deepEqual(
      [...zipped.entries()]
        .map(([path, bytes]) => [path, bytes.toString("utf8")])
        .sort(([a], [b]) => String(a).localeCompare(String(b))),
      Object.entries(files).sort(([a], [b]) => a.localeCompare(b)),
    );
    assert.equal(packed.sourceDigest, scan.skills[0].sourceDigest);
    assert.equal(packed.name, "demo");
    assert.equal(packed.description, "Demo skill.");
  });
});

test("packLocalSkill_InvalidDirectoryName_Throws", async () => {
  await withHome(async (home) => {
    for (const directoryName of ["..", "a/b", "a\\b", ".x", "", "nul\u0000x"]) {
      await assert.rejects(packLocalSkill(home, directoryName), {
        message: "Invalid local skill directory name.",
      });
    }
  });
});

test("packLocalSkill_SkillBecameInvalid_Throws", async () => {
  await withHome(async (home) => {
    const root = await writeSkill(home, "demo", demoSkill);
    await writeFile(join(root, "SKILL.md"), "Use it.\n");
    await assert.rejects(packLocalSkill(home, "demo"), {
      message: "SKILL.md has no front matter.",
    });
  });
});

test("packLocalSkill_NonAsciiFileName_PreservesEntryName", async () => {
  await withHome(async (home) => {
    const root = await writeSkill(home, "demo", demoSkill);
    await mkdir(join(root, "参考"), { recursive: true });
    await writeFile(join(root, "参考", "说明.md"), "Keep this name.\n");
    const zipped = await readZip((await packLocalSkill(home, "demo")).bytes);
    assert.equal(zipped.get("参考/说明.md")?.toString("utf8"), "Keep this name.\n");
  });
});
