const RESERVED_WINDOWS_FOLDER_NAMES = new Set([
  "CON",
  "PRN",
  "AUX",
  "NUL",
  "COM1",
  "COM2",
  "COM3",
  "COM4",
  "COM5",
  "COM6",
  "COM7",
  "COM8",
  "COM9",
  "LPT1",
  "LPT2",
  "LPT3",
  "LPT4",
  "LPT5",
  "LPT6",
  "LPT7",
  "LPT8",
  "LPT9",
]);

export function formatProjectFolderName(value: string): string {
  const formatted = value
    .trim()
    .replace(/[^\p{L}\p{N}._-]+/gu, "_")
    .replace(/_+/g, "_")
    .replace(/^[_.\s]+|[_.\s]+$/g, "");

  if (!formatted || formatted === "." || formatted === ".." || formatted.length > 255) {
    return "";
  }

  const baseName = formatted.split(".")[0].toUpperCase();
  if (RESERVED_WINDOWS_FOLDER_NAMES.has(baseName)) {
    return "";
  }

  return formatted;
}

export function getDefaultProjectWorkspace(projectName: string): string {
  const folderName = formatProjectFolderName(projectName);
  return folderName ? `~/.agw/${folderName}` : "";
}

export function syncDefaultProjectWorkspace({
  previousName,
  nextName,
  currentWorkspace,
}: {
  previousName: string;
  nextName: string;
  currentWorkspace: string;
}): string {
  const previousDefaultWorkspace = getDefaultProjectWorkspace(previousName);
  const nextDefaultWorkspace = getDefaultProjectWorkspace(nextName);
  const currentWorkspaceValue = currentWorkspace.trim();

  if (!currentWorkspaceValue || currentWorkspaceValue === previousDefaultWorkspace) {
    return nextDefaultWorkspace;
  }

  return currentWorkspace;
}

export function resolveCreateProjectWorkspace(
  projectName: string,
  workspace: string,
): string | null {
  const workspaceValue = workspace.trim();
  if (workspaceValue) {
    return workspaceValue;
  }

  return getDefaultProjectWorkspace(projectName) || null;
}
