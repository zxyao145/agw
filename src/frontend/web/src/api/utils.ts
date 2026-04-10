import { ApiError } from "@/api/client";

export function getApiErrorMessage(error: unknown): string {
  if (error instanceof ApiError) {
    if (typeof error.body === "string" && error.body.trim().length) {
      return error.body;
    }
    if (error.body && typeof error.body === "object") {
      try {
        return JSON.stringify(error.body);
      } catch {
        // ignore
      }
    }
    return `${error.status} ${error.statusText}`;
  }
  if (error instanceof Error) return error.message;
  if (typeof error === "string") return error;

  return "Unknown error";
}
