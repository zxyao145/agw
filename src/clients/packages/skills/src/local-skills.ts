export type LocalSkill = {
  directoryName: string;
  name: string | null;
  description: string | null;
  sourceDigest: string | null;
  fileCount: number;
  totalBytes: number;
  problem: string | null;
};

export type LocalSkillScan = {
  directory: string;
  exists: boolean;
  skills: LocalSkill[];
};

export type LocalSkillPackage = {
  directoryName: string;
  name: string;
  description: string;
  sourceDigest: string;
  fileName: string;
  bytes: Uint8Array;
};

export type LocalSkillStatus =
  | { kind: "Invalid"; reason: string }
  | { kind: "New" }
  | { kind: "Synced"; skillId: string }
  | { kind: "Changed"; skillId: string }
  | { kind: "Conflict"; reason: string };

export type LocalSkillsSource = {
  scan(): Promise<LocalSkillScan>;
  pack(directoryName: string): Promise<LocalSkillPackage>;
};

export type ServerSkillSummary = {
  id: string;
  name: string;
  kind: "BuiltIn" | "Local" | "Remote";
  isBuiltIn: boolean;
  sourceDigest?: string | null;
};

export function resolveLocalSkillStatus(
  skill: LocalSkill,
  serverSkills: readonly ServerSkillSummary[],
): LocalSkillStatus {
  if (skill.problem !== null) return { kind: "Invalid", reason: skill.problem };

  const name = skill.name?.trim();
  const serverSkill = serverSkills.find((item) => item.name.trim() === name);
  if (!serverSkill) return { kind: "New" };

  if (serverSkill.isBuiltIn || serverSkill.kind === "BuiltIn") {
    return { kind: "Conflict", reason: "A built-in skill already uses this name." };
  }
  if (serverSkill.kind !== "Local") {
    return { kind: "Conflict", reason: "A remote skill already uses this name." };
  }
  if (skill.sourceDigest !== null && serverSkill.sourceDigest === skill.sourceDigest) {
    return { kind: "Synced", skillId: serverSkill.id };
  }
  return { kind: "Changed", skillId: serverSkill.id };
}

export function buildLocalSkillFormData(
  skill: LocalSkill,
  status: Extract<LocalSkillStatus, { kind: "New" | "Changed" }>,
  pkg: LocalSkillPackage,
): FormData {
  if (pkg.name !== skill.name) {
    throw new Error(
      `SKILL.md of ${skill.directoryName} changed after the scan. Rescan and try again.`,
    );
  }

  const data = new FormData();
  if (status.kind === "New") data.append("Kind", "Local");
  data.append("Name", pkg.name);
  data.append("Description", pkg.description);
  const bytes = new Uint8Array(pkg.bytes);
  data.append("Archive", new File([bytes.buffer], pkg.fileName, { type: "application/zip" }));
  data.append("SourceDigest", pkg.sourceDigest);
  return data;
}
