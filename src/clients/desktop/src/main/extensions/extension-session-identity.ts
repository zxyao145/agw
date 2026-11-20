import { isExtensionInstanceId } from "../../shared/extension-instance";

export function extensionSessionId(instanceId: string): string {
  if (!isExtensionInstanceId(instanceId)) throw new Error("Invalid extension instance id.");
  return instanceId;
}
