import { resolve, sep } from "node:path";

export function resolveRendererFile(root: string, pathname: string): string {
  const decodedPath = decodeURIComponent(pathname);
  const segments = decodedPath.split("/");
  if (segments.includes("..")) throw new Error("Requested path is outside renderer root.");

  let relativePath = decodedPath.replace(/^\/+/, "");
  if (!relativePath) relativePath = "index.html";
  else if (relativePath.endsWith("/")) relativePath += "index.html";

  const rendererRoot = resolve(root);
  const file = resolve(rendererRoot, relativePath);
  if (file !== rendererRoot && !file.startsWith(`${rendererRoot}${sep}`)) {
    throw new Error("Requested path is outside renderer root.");
  }
  return file;
}
