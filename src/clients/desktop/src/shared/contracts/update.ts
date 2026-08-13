export type DesktopUpdateStatus = "available" | "up-to-date" | "ahead";

export type DesktopUpdateCheckResult = {
  status: DesktopUpdateStatus;
  currentVersion: string;
  latestVersion: string;
  publishedAt: string;
  releaseUrl: string;
  assetName: string | null;
  downloadUrl: string | null;
};
