export function verifyWindowsIcon(
  executable: Uint8Array,
  expectedIcon: Uint8Array,
  label: string,
): void;

export function verifyReleaseIcons(options: {
  platform: "win32" | "darwin" | "linux";
  applicationDirectory: string;
  artifacts: string[];
}): Promise<void>;
