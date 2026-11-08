import type { PermissionMode } from "./protocol";

export type PermissionStatus = {
  activePermissionMode: PermissionMode | null;
  nextPermissionMode: PermissionMode | null;
  permissionChangePending: boolean;
};
