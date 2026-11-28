import assert from "node:assert/strict";
import test from "node:test";

import {
  buildLocalSkillFormData,
  resolveLocalSkillStatus,
  type LocalSkill,
  type LocalSkillPackage,
} from "./local-skills";

const digest = "sha256:fd5db01f805c3fa8eaec5b16afe3d3bc9c02ebf1b9e7081ade4674d50ba3384e";

const local: LocalSkill = {
  directoryName: "demo",
  name: "demo",
  description: "Demo skill.",
  sourceDigest: digest,
  fileCount: 2,
  totalBytes: 60,
  problem: null,
};

function pkgOf(skill: LocalSkill): LocalSkillPackage {
  return {
    directoryName: skill.directoryName,
    name: skill.name ?? "demo",
    description: skill.description ?? "Demo skill.",
    sourceDigest: skill.sourceDigest ?? digest,
    fileName: `${skill.name ?? "demo"}.zip`,
    bytes: new Uint8Array([80, 75]),
  };
}

test("resolveLocalSkillStatus_Problem_ReturnsInvalid", () => {
  assert.deepEqual(resolveLocalSkillStatus({ ...local, problem: "SKILL.md is missing." }, []), {
    kind: "Invalid",
    reason: "SKILL.md is missing.",
  });
});

test("resolveLocalSkillStatus_NoServerSkill_ReturnsNew", () => {
  assert.deepEqual(resolveLocalSkillStatus(local, []), { kind: "New" });
});

test("resolveLocalSkillStatus_SameDigest_ReturnsSynced", () => {
  assert.deepEqual(
    resolveLocalSkillStatus(local, [
      { id: "s1", name: "demo", kind: "Local", isBuiltIn: false, sourceDigest: digest },
    ]),
    { kind: "Synced", skillId: "s1" },
  );
});

test("resolveLocalSkillStatus_DifferentOrMissingDigest_ReturnsChanged", () => {
  assert.deepEqual(
    resolveLocalSkillStatus(local, [
      { id: "s1", name: "demo", kind: "Local", isBuiltIn: false, sourceDigest: "sha256:different" },
    ]),
    { kind: "Changed", skillId: "s1" },
  );
  assert.deepEqual(
    resolveLocalSkillStatus(local, [
      { id: "s1", name: "demo", kind: "Local", isBuiltIn: false, sourceDigest: null },
    ]),
    { kind: "Changed", skillId: "s1" },
  );
});

test("resolveLocalSkillStatus_BuiltInOrRemote_ReturnsConflict", () => {
  assert.deepEqual(
    resolveLocalSkillStatus(local, [
      { id: "s1", name: "demo", kind: "BuiltIn", isBuiltIn: true, sourceDigest: null },
    ]),
    { kind: "Conflict", reason: "A built-in skill already uses this name." },
  );
  assert.deepEqual(
    resolveLocalSkillStatus(local, [
      { id: "s1", name: "demo", kind: "Remote", isBuiltIn: false, sourceDigest: null },
    ]),
    { kind: "Conflict", reason: "A remote skill already uses this name." },
  );
});

test("buildLocalSkillFormData_New_IncludesKindAndDigest", () => {
  const data = buildLocalSkillFormData(local, { kind: "New" }, pkgOf(local));
  const archive = data.get("Archive");

  assert.equal(data.get("Kind"), "Local");
  assert.equal(data.get("Name"), "demo");
  assert.equal(data.get("Description"), "Demo skill.");
  assert.equal(data.get("SourceDigest"), digest);
  assert.ok(archive instanceof File);
  assert.equal(archive.name, "demo.zip");
  assert.equal(archive.type, "application/zip");
});

test("buildLocalSkillFormData_Changed_OmitsKind", () => {
  const data = buildLocalSkillFormData(local, { kind: "Changed", skillId: "s1" }, pkgOf(local));

  assert.equal(data.has("Kind"), false);
  assert.equal(data.get("Name"), "demo");
  assert.equal(data.get("Description"), "Demo skill.");
  assert.equal(data.get("Archive") instanceof File, true);
  assert.equal(data.get("SourceDigest"), digest);
});

test("buildLocalSkillFormData_NameChangedAfterScan_Throws", () => {
  assert.throws(
    () =>
      buildLocalSkillFormData(
        local,
        { kind: "New" },
        {
          ...pkgOf(local),
          name: "renamed",
        },
      ),
    { message: "SKILL.md of demo changed after the scan. Rescan and try again." },
  );
});
