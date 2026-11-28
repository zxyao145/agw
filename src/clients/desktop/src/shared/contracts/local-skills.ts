export type LocalSkill = {
  /** 本机 Skills 目录下的子目录名。The subdirectory name under the local skills directory. */
  directoryName: string;
  name: string | null;
  description: string | null;
  /** 有 problem 时为 null。Null when there is a problem. */
  sourceDigest: string | null;
  fileCount: number;
  totalBytes: number;
  /** 不能导入的原因。Why the skill can't be imported. */
  problem: string | null;
};

export type LocalSkillScan = {
  /** 本机 Skills 目录的绝对路径。The absolute path of the local skills directory. */
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
