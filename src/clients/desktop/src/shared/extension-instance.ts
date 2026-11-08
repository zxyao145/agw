import { isExtensionId } from "./extension-manifest";

const SERVER_EXTENSION_INSTANCE_ID_PATTERN = /^server-[0-9a-f]{64}$/u;

export function isServerExtensionInstanceId(value: string): boolean {
  return SERVER_EXTENSION_INSTANCE_ID_PATTERN.test(value);
}

export function isExtensionInstanceId(value: string): boolean {
  return isExtensionId(value) || isServerExtensionInstanceId(value);
}
