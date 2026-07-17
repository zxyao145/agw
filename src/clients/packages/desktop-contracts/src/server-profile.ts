export type DesktopPlatform =
  | "aix"
  | "android"
  | "darwin"
  | "freebsd"
  | "haiku"
  | "linux"
  | "openbsd"
  | "sunos"
  | "win32"
  | "cygwin"
  | "netbsd";

export type ServerProfile = {
  id: string;
  kind: "local" | "remote";
  name: string;
  baseUrl: string;
  apiMajorVersion: 1;
  allowInsecureHttp: boolean;
};
