export type ProjectDirectory = { id: string; path: string };

export type ProjectDirectoryOption = {
  id: string | null;
  name: string;
  path: string;
  isPrimary: boolean;
};

export function getDirectoryName(path: string): string {
  return path.replaceAll("\\", "/").split("/").filter(Boolean).at(-1) || path;
}

export function getProjectDirectories(project: {
  id: string;
  workspace?: string | null;
  additionalDirectories?: readonly ProjectDirectory[] | null;
}): ProjectDirectoryOption[] {
  const primary = project.workspace?.trim() || `~/.agw/projects/${project.id.replaceAll("-", "")}`;
  return [
    { id: null, name: getDirectoryName(primary), path: primary, isPrimary: true },
    ...(project.additionalDirectories ?? [])
      .filter((directory) => directory.id)
      .map((directory) => ({
        id: directory.id,
        name: getDirectoryName(directory.path),
        path: directory.path,
        isPrimary: false,
      })),
  ];
}
