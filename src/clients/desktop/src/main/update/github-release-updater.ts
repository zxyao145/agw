import type {
  DesktopPlatform,
  DesktopUpdateCheckResult,
  PackageFlavor,
} from "../../shared/contracts";

const DESKTOP_VERSION_PATTERN =
  /^(0|[1-9]\d*)\.(0|[1-9]\d*)\.(0|[1-9]\d*)(?:-(preview|alpha|beta)\.(0|[1-9]\d*))?$/u;
const GITHUB_API_URL = "https://api.github.com/repos/zxyao145/agw/releases/latest";
const GITHUB_RELEASES_URL = "https://github.com/zxyao145/agw/releases";
const DEFAULT_TIMEOUT_MS = 10_000;

type DesktopVersion = {
  major: number;
  minor: number;
  patch: number;
  prerelease: {
    channel: "preview" | "alpha" | "beta";
    number: number;
  } | null;
};

type GitHubRelease = {
  tag: string;
  version: string;
  publishedAt: string;
  assetNames: Set<string>;
};

export type DesktopFetcher = (input: string | Request, init?: RequestInit) => Promise<Response>;

export type DesktopUpdateCheckOptions = {
  currentVersion: string;
  packageFlavor: PackageFlavor;
  platform: DesktopPlatform;
  architecture: string;
  windowsDistribution: "squirrel" | "portable";
  timeoutMs?: number;
};

function isRecord(value: unknown): value is Record<string, unknown> {
  return typeof value === "object" && value !== null && !Array.isArray(value);
}

function parseDesktopVersion(value: string): DesktopVersion {
  const match = DESKTOP_VERSION_PATTERN.exec(value);
  if (!match) throw new Error(`Invalid Desktop version: ${value}.`);

  const numbers = [match[1], match[2], match[3], match[5]]
    .filter((part): part is string => part !== undefined)
    .map(Number);
  if (numbers.some((part) => !Number.isSafeInteger(part))) {
    throw new Error(`Invalid Desktop version: ${value}.`);
  }

  return {
    major: numbers[0],
    minor: numbers[1],
    patch: numbers[2],
    prerelease: match[4]
      ? {
          channel: match[4] as "preview" | "alpha" | "beta",
          number: numbers[3],
        }
      : null,
  };
}

function compareNumber(left: number, right: number): -1 | 0 | 1 {
  if (left === right) return 0;
  return left < right ? -1 : 1;
}

export function compareDesktopVersions(leftValue: string, rightValue: string): -1 | 0 | 1 {
  const left = parseDesktopVersion(leftValue);
  const right = parseDesktopVersion(rightValue);

  for (const field of ["major", "minor", "patch"] as const) {
    const result = compareNumber(left[field], right[field]);
    if (result !== 0) return result;
  }

  if (!left.prerelease && !right.prerelease) return 0;
  if (!left.prerelease) return 1;
  if (!right.prerelease) return -1;

  if (left.prerelease.channel !== right.prerelease.channel) {
    return left.prerelease.channel < right.prerelease.channel ? -1 : 1;
  }
  return compareNumber(left.prerelease.number, right.prerelease.number);
}

export function isSupportedDesktopVersion(value: string): boolean {
  try {
    parseDesktopVersion(value);
    return true;
  } catch {
    return false;
  }
}

export function resolveDesktopUpdateAssetName(
  version: string,
  options: DesktopUpdateCheckOptions,
): string | null {
  parseDesktopVersion(version);
  const prefix = `Agw-Desktop-${version}-${options.packageFlavor}`;

  if (options.platform === "darwin" && ["x64", "arm64"].includes(options.architecture)) {
    return `${prefix}-macos-${options.architecture}.dmg`;
  }
  if (options.platform === "linux" && options.architecture === "x64") {
    return `${prefix}-linux-x64.deb`;
  }
  if (options.platform === "win32" && options.architecture === "x64") {
    if (options.packageFlavor === "client" && options.windowsDistribution === "portable") {
      return `${prefix}-windows-x64-Portable.zip`;
    }
    return `${prefix}-windows-x64-Setup.exe`;
  }
  return null;
}

function parseGitHubRelease(value: unknown): GitHubRelease {
  if (
    !isRecord(value) ||
    typeof value.tag_name !== "string" ||
    value.draft !== false ||
    value.prerelease !== false ||
    typeof value.published_at !== "string" ||
    Number.isNaN(Date.parse(value.published_at)) ||
    !Array.isArray(value.assets)
  ) {
    throw new Error("GitHub returned invalid release data.");
  }

  const version = value.tag_name.startsWith("v") ? value.tag_name.slice(1) : "";
  if (!isSupportedDesktopVersion(version)) {
    throw new Error("GitHub returned invalid release data.");
  }

  const assetNames = new Set<string>();
  for (const asset of value.assets) {
    if (!isRecord(asset) || typeof asset.name !== "string") {
      throw new Error("GitHub returned invalid release data.");
    }
    assetNames.add(asset.name);
  }

  return {
    tag: value.tag_name,
    version,
    publishedAt: value.published_at,
    assetNames,
  };
}

function updateStatus(comparison: -1 | 0 | 1): DesktopUpdateCheckResult["status"] {
  if (comparison < 0) return "available";
  if (comparison > 0) return "ahead";
  return "up-to-date";
}

export async function checkForDesktopUpdate(
  fetcher: DesktopFetcher,
  options: DesktopUpdateCheckOptions,
): Promise<DesktopUpdateCheckResult> {
  parseDesktopVersion(options.currentVersion);
  const abortController = new AbortController();
  let timedOut = false;
  const timeout = setTimeout(() => {
    timedOut = true;
    abortController.abort();
  }, options.timeoutMs ?? DEFAULT_TIMEOUT_MS);

  let response: Response;
  try {
    response = await fetcher(GITHUB_API_URL, {
      headers: {
        Accept: "application/vnd.github+json",
        "User-Agent": `Agw-Desktop/${options.currentVersion}`,
        "X-GitHub-Api-Version": "2026-03-10",
      },
      signal: abortController.signal,
    });
  } catch (error) {
    if (timedOut || abortController.signal.aborted) {
      throw new Error("The GitHub update check timed out.");
    }
    throw new Error(
      error instanceof Error
        ? `Unable to check GitHub for updates: ${error.message}`
        : "Unable to check GitHub for updates.",
    );
  } finally {
    clearTimeout(timeout);
  }

  if (!response.ok) {
    throw new Error(`GitHub rejected the update check (${response.status}).`);
  }

  let releaseBody: unknown;
  try {
    releaseBody = await response.json();
  } catch {
    throw new Error("GitHub returned invalid release data.");
  }
  const release = parseGitHubRelease(releaseBody);
  const status = updateStatus(compareDesktopVersions(options.currentVersion, release.version));
  const expectedAssetName =
    status === "available" ? resolveDesktopUpdateAssetName(release.version, options) : null;
  const assetName =
    expectedAssetName && release.assetNames.has(expectedAssetName) ? expectedAssetName : null;

  return {
    status,
    currentVersion: options.currentVersion,
    latestVersion: release.version,
    publishedAt: release.publishedAt,
    releaseUrl: `${GITHUB_RELEASES_URL}/tag/${encodeURIComponent(release.tag)}`,
    assetName,
    downloadUrl: assetName
      ? `${GITHUB_RELEASES_URL}/download/${encodeURIComponent(release.tag)}/${encodeURIComponent(assetName)}`
      : null,
  };
}
