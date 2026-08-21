import { ApiError } from "@agw/api";

export function getErrorMessage(error: unknown): string {
  if (error instanceof ApiError) {
    const body = error.body;
    if (typeof body === "object" && body !== null) {
      const detail = "detail" in body && typeof body.detail === "string" ? body.detail : null;
      const title = "title" in body && typeof body.title === "string" ? body.title : null;
      if (detail?.trim()) return detail;
      if (title?.trim()) return title;
    }
    if (error.status === 401) return "The API token is invalid or has been revoked.";
    return `${error.status} ${error.statusText}`.trim();
  }
  if (error instanceof Error && error.message.trim()) return error.message;
  return "Something went wrong.";
}
