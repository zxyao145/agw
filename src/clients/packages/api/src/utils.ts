import { ApiError } from "./client";

export function getApiErrorMessage(error: unknown): string {
  if (error instanceof ApiError) {
    if (typeof error.body === "string" && error.body.trim().length) {
      return error.body;
    }
    if (error.body && typeof error.body === "object") {
      const resultBody = error.body as {
        title?: unknown;
        detail?: unknown;
        errors?: unknown;
      };

      if (typeof resultBody.detail === "string" && resultBody.detail.trim().length) {
        return resultBody.detail;
      }

      if (typeof resultBody.title === "string" && resultBody.title.trim().length) {
        return resultBody.title;
      }

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
