import { ApiError } from "@/api/client";

export function getApiErrorMessage(error: unknown): string {
  if (error instanceof ApiError) {
    if (typeof error.body === "string" && error.body.trim().length) {
      return error.body;
    }
    return `${error.status} ${error.statusText}`;
  }
  if (error instanceof Error) return error.message;
  return "Unknown error";
}
